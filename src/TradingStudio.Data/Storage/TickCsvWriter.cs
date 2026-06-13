using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

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

    public long WrittenCount => Interlocked.Read(ref _written);
    public long ErrorCount => Interlocked.Read(ref _errors);

    public TickCsvWriter(string basePath = "TickData", int flushSec = 10)
    {
        _basePath = basePath;
        Directory.CreateDirectory(basePath);
        _timer = new Timer(_ => FlushAll(), null, flushSec * 1000, flushSec * 1000);
    }

    /// <summary>写入一条 Tick 行（线程安全，单 writer 失败不影响其他合约）</summary>
    public void Write(string instrumentId, string exchangeId, string tradingDay,
        string updateTime, int updateMs,
        double lastPrice, double preSettle, double preClose, double preOI,
        double openPrice, double highest, double lowest,
        int volume, double turnover, double openInterest,
        double closePrice, double settle, double upperLimit, double lowerLimit,
        double bp1, int bv1, double ap1, int av1,
        double bp2, int bv2, double ap2, int av2,
        double bp3, int bv3, double ap3, int av3,
        double bp4, int bv4, double ap4, int av4,
        double bp5, int bv5, double ap5, int av5,
        double avgPrice)
    {
        if (string.IsNullOrEmpty(instrumentId) || string.IsNullOrEmpty(tradingDay)) return;

        var ex = string.IsNullOrEmpty(exchangeId) ? GuessExchange(instrumentId) : exchangeId;
        var key = $"{instrumentId}_{tradingDay}";

        // 已标记为失败的 writer，跳过后续写入
        if (_failed.ContainsKey(key)) return;

        if (!_writers.TryGetValue(key, out var entry) || entry.Day != tradingDay)
        {
            lock (_createLock)
            {
                if (!_writers.TryGetValue(key, out entry) || entry.Day != tradingDay)
                {
                    _writers.TryRemove(key, out var old);
                    SafeDispose(old.Writer);
                    try
                    {
                        entry = CreateWriter(instrumentId, tradingDay, ex);
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
                w.Write(tradingDay); w.Write(','); w.Write(instrumentId); w.Write(',');
                w.Write(ex); w.Write(','); w.Write(','); // 4空
                w.Write(F(lastPrice)); w.Write(','); w.Write(F(preSettle)); w.Write(',');
                w.Write(F(preClose)); w.Write(','); w.Write(F(preOI)); w.Write(',');
                w.Write(F(openPrice)); w.Write(','); w.Write(F(highest)); w.Write(',');
                w.Write(F(lowest)); w.Write(','); w.Write(volume); w.Write(',');
                w.Write(Fd(turnover)); w.Write(','); w.Write(F(openInterest)); w.Write(',');
                w.Write(F(closePrice)); w.Write(','); w.Write(F(settle)); w.Write(',');
                w.Write(F(upperLimit)); w.Write(','); w.Write(F(lowerLimit)); w.Write(',');
                w.Write("0,0,"); // delta
                w.Write(updateTime); w.Write(','); w.Write(updateMs); w.Write(',');
                BP(w, bp1, bv1, ap1, av1); BP(w, bp2, bv2, ap2, av2);
                BP(w, bp3, bv3, ap3, av3); BP(w, bp4, bv4, ap4, av4);
                BP(w, bp5, bv5, ap5, av5);
                w.Write(F(avgPrice)); w.Write(','); w.WriteLine(tradingDay);
            }
            Interlocked.Increment(ref _written);
        }
        catch (Exception)
        {
            // 标记失败，后续 Tick 跳过此合约（避免反复抛异常）
            _failed.TryAdd(key, 1);
            Interlocked.Increment(ref _errors);
            try { _writers.TryRemove(key, out var old); SafeDispose(old.Writer); } catch { }
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
        var sw = new StreamWriter(path, true, Encoding.UTF8) { AutoFlush = false };
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
        if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 1e100) return "0.00";
        return v.ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string GuessExchange(string inst)
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

    private const string Hdr = "日期,合约代码,交易所代码,合约在交易所的代码,最新价,上次结算价,昨收盘,昨持仓量," +
        "开盘价,最高价,最低价,成交量,成交额,持仓量,涨跌,本次结算价,涨停价,跌停价," +
        "昨虚实度,今虚实度,更新时间,更新毫秒," +
        "买一价,买一量,卖一价,卖一量,买二价,买二量,卖二价,卖二量," +
        "买三价,买三量,卖三价,卖三量,买四价,买四量,卖四价,卖四量," +
        "买五价,买五量,卖五价,卖五量,均价,业务日期";
}
