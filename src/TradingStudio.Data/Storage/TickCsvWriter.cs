using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using TradingStudio.Core.Models;

namespace TradingStudio.Data.Storage;

/// <summary>
/// Tick CSV 写入器 — 金数源格式，42 列 CTP 全字段。
/// 每合约每天一个文件: {basePath}/{exchange}/{contract}_{tradingDay}.csv
/// 线程安全，定时刷新。
/// </summary>
public class TickCsvWriter : IDisposable
{
    private readonly string _basePath;
    private readonly ConcurrentDictionary<string, (StreamWriter Writer, string Day)> _writers = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new(); // 1=写失败,跳过后续写入
    private readonly object _createLock = new();
    private readonly Timer _timer;
    private long _written;
    private long _errors;

    /// <summary>GBK 编码（无 BOM），匹配金数源格式</summary>
    private static readonly Encoding Gbk = InitGbk();

    private static Encoding InitGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }

    public long WrittenCount => Interlocked.Read(ref _written);
    public long ErrorCount => Interlocked.Read(ref _errors);

    public TickCsvWriter(string basePath = "TickData", int flushSec = 10)
    {
        _basePath = basePath;
        Directory.CreateDirectory(basePath);
        _timer = new Timer(_ => FlushAll(), null, flushSec * 1000, flushSec * 1000);
    }

    /// <summary>写入一条 Tick 行（线程安全，单 writer 失败不影响其他合约）</summary>
    public void Write(in CsvTickRecord r)
    {
        if (string.IsNullOrEmpty(r.InstrumentId) || string.IsNullOrEmpty(r.TradingDay)) return;

        var ex = string.IsNullOrEmpty(r.ExchangeId) ? GuessExchange(r.InstrumentId) : r.ExchangeId;
        var key = $"{r.InstrumentId}_{r.TradingDay}";

        if (_failed.ContainsKey(key)) return;

        if (!_writers.TryGetValue(key, out var entry) || entry.Day != r.TradingDay)
        {
            lock (_createLock)
            {
                if (!_writers.TryGetValue(key, out entry) || entry.Day != r.TradingDay)
                {
                    _writers.TryRemove(key, out var old);
                    SafeDispose(old.Writer);
                    try
                    {
                        entry = CreateWriter(r.InstrumentId, r.TradingDay, ex);
                        _writers[key] = entry;
                    }
                    catch (Exception)
                    {
                        _failed.TryAdd(key, 1);
                        Interlocked.Increment(ref _errors);
                        return;
                    }
                }
            }
        }

        try
        {
            lock (entry.Writer)
            {
                var w = entry.Writer;
                w.Write(r.TradingDay);           w.Write(',');
                w.Write(r.InstrumentId);         w.Write(',');
                w.Write(','); w.Write(',');       // 交易所代码、合约在交易所的代码留空（匹配金数源）
                w.Write(F(r.LastPrice));         w.Write(',');
                w.Write(F(r.PreSettlementPrice));w.Write(',');
                w.Write(F(r.PreClosePrice));     w.Write(',');
                w.Write((long)r.PreOpenInterest);w.Write(',');
                w.Write(F(r.OpenPrice));         w.Write(',');
                w.Write(F(r.HighestPrice));      w.Write(',');
                w.Write(F(r.LowestPrice));       w.Write(',');
                w.Write(r.Volume);               w.Write(',');
                w.Write(Fd(r.Turnover));         w.Write(',');
                w.Write((long)r.OpenInterest);   w.Write(',');
                w.Write(F(r.ClosePrice));        w.Write(',');
                w.Write(F(r.SettlementPrice));   w.Write(',');
                w.Write(F(r.UpperLimitPrice));   w.Write(',');
                w.Write(F(r.LowerLimitPrice));   w.Write(',');
                w.Write("0,0,");                  // 昨/今虚实度
                w.Write(r.UpdateTime);           w.Write(',');
                w.Write(r.UpdateMillisec);       w.Write(',');
                BP(w, r.BidPrice1, r.BidVolume1, r.AskPrice1, r.AskVolume1);
                BP(w, r.BidPrice2, r.BidVolume2, r.AskPrice2, r.AskVolume2);
                BP(w, r.BidPrice3, r.BidVolume3, r.AskPrice3, r.AskVolume3);
                BP(w, r.BidPrice4, r.BidVolume4, r.AskPrice4, r.AskVolume4);
                BP(w, r.BidPrice5, r.BidVolume5, r.AskPrice5, r.AskVolume5);
                w.Write(F(r.AveragePrice));      w.Write(',');
                w.WriteLine(r.TradingDay);
            }
            Interlocked.Increment(ref _written);
        }
        catch (Exception)
        {
            _failed.TryAdd(key, 1);
            Interlocked.Increment(ref _errors);
            try { _writers.TryRemove(key, out var old); SafeDispose(old.Writer); } catch { /* cleanup best-effort */ }
        }
    }

    public void FlushAll()
    {
        foreach (var (key, entry) in _writers)
        {
            if (_failed.ContainsKey(key)) continue;
            try { entry.Writer.Flush(); } catch { }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var (_, entry) in _writers) try { entry.Writer.Dispose(); } catch { }
        _writers.Clear();
        _failed.Clear();
    }

    private static void SafeDispose(StreamWriter? sw)
    {
        try { sw?.Dispose(); } catch { }
    }

    private (StreamWriter Writer, string Day) CreateWriter(string inst, string day, string ex)
    {
        var dir = Path.Combine(_basePath, ex);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{inst}_{day}.csv");
        var sw = new StreamWriter(path, true, Gbk) { AutoFlush = false };
        if (new FileInfo(path).Length == 0) sw.WriteLine(Hdr);
        return (sw, day);
    }

    private static void BP(StreamWriter w, double bp, int bv, double ap, int av)
        { w.Write(F(bp)); w.Write(','); w.Write(bv); w.Write(','); w.Write(F(ap)); w.Write(','); w.Write(av); w.Write(','); }

    private static string F(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 1e100) return "0.0000";
        return v.ToString("F4", CultureInfo.InvariantCulture);
    }
    private static string Fd(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 1e100) return "0.0000";
        return v.ToString("F4", CultureInfo.InvariantCulture); // 成交金额 F4，匹配金数源
    }

    public static string GuessExchange(string inst)
    {
        if (inst.Length == 0) return "??";
        // CFFEX (股指: IF/IC/IH/IM; 国债: T/TS/TF/TL, 其中 T 后直接跟数字)
        if (inst.StartsWith("IF") || inst.StartsWith("IC") || inst.StartsWith("IH") || inst.StartsWith("IM")
         || inst.StartsWith("TS") || inst.StartsWith("TF") || inst.StartsWith("TL")
         || (inst.StartsWith("T") && inst.Length >= 5 && char.IsDigit(inst[1]))) return "CFFEX";
        // INE (小写字母 + 数字)
        if (inst.StartsWith("sc") || inst.StartsWith("lu") || inst.StartsWith("bc")
         || inst.StartsWith("nr") || inst.StartsWith("ec")) return "INE";
        // GFEX
        if (inst.StartsWith("si") || inst.StartsWith("lc") || inst.StartsWith("ps")) return "GFEX";
        // CZCE (双大写字母开头，如 TA608, FG511)
        if (inst.Length >= 2 && char.IsUpper(inst[0]) && char.IsUpper(inst[1])) return "CZCE";
        // SHFE (全小写前缀，必须在 DCE 字母通配之前显式匹配)
        if (inst.StartsWith("cu") || inst.StartsWith("al") || inst.StartsWith("zn") || inst.StartsWith("pb")
         || inst.StartsWith("ni") || inst.StartsWith("sn") || inst.StartsWith("au") || inst.StartsWith("ag")
         || inst.StartsWith("rb") || inst.StartsWith("wr") || inst.StartsWith("hc") || inst.StartsWith("ss")
         || inst.StartsWith("bu") || inst.StartsWith("ru") || inst.StartsWith("sp") || inst.StartsWith("fu")
         || inst.StartsWith("ao") || inst.StartsWith("br")) return "SHFE";
        // DCE (具体小写双字母前缀)
        if (inst.StartsWith("fb") || inst.StartsWith("bb") || inst.StartsWith("jd") || inst.StartsWith("lh")
         || inst.StartsWith("rr") || inst.StartsWith("pp") || inst.StartsWith("pg") || inst.StartsWith("eg")
         || inst.StartsWith("cs") || inst.StartsWith("eb")) return "DCE";
        // DCE (单字母前缀: a=豆一, b=豆二, c=玉米, d=鸡蛋... 等)
        if ("abcdeijlmprvy".Contains(inst[0])) return "DCE";
        return "??";
    }

    private const string Hdr = "交易日,合约代码,交易所代码,合约在交易所的代码,最新价,上次结算价,昨收盘,昨持仓量," +
        "今开盘,最高价,最低价,数量,成交金额,持仓量,今收盘,本次结算价,涨停板价,跌停板价," +
        "昨虚实度,今虚实度,最后修改时间,最后修改毫秒," +
        "申买价一,申买量一,申卖价一,申卖量一,申买价二,申买量二,申卖价二,申卖量二," +
        "申买价三,申买量三,申卖价三,申卖量三,申买价四,申买量四,申卖价四,申卖量四," +
        "申买价五,申买量五,申卖价五,申卖量五,当日均价,业务日期";
}
