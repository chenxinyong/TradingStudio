using System.Globalization;
using System.Text;
using TradingStudio.Core.Models;

namespace TradingStudio.Data.Import;

/// <summary>
/// 金数源 CSV Tick 解析器 — 44 列 GBK 编码 CTP 格式。
/// 流式读取，不持有大文件在内存。
/// </summary>
public static class CsvTickImporter
{
    /// <summary>解析结果</summary>
    public readonly record struct ParseResult(
        TickRecord Tick,
        string InstrumentId,
        DateOnly TradingDay,
        double UpperLimitPrice,
        double LowerLimitPrice);

    /// <summary>
    /// 从文件名提取合约代码和交易日。
    /// 文件名格式: 金数源_商品tick快照样本_{symbol}_{tradingDay}_CTP格式.csv
    /// </summary>
    public static (string Symbol, DateOnly TradingDay) ParseFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        // Split by '_', 取倒数第3和第2段
        var parts = name.Split('_');
        if (parts.Length >= 5)
        {
            var symbol = parts[^3];      // cu1603
            var dayStr = parts[^2];       // 20160111
            if (DateOnly.TryParseExact(dayStr, "yyyyMMdd", out var day))
                return (symbol, day);
        }
        throw new FormatException($"无法从文件名解析合约/交易日: {filePath}");
    }

    /// <summary>
    /// 从 RAR 内文件名提取合约代码和交易日。
    /// 格式: {contract}_{YYYYMMDD}.csv  例: "cu2501_20250601.csv", "ag主力连续_20210104.csv"
    /// </summary>
    public static (string Symbol, DateOnly TradingDay) ParseRarFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath); // "cu2501_20250601"
        var lastUnderscore = name.LastIndexOf('_');
        if (lastUnderscore < 0)
            throw new FormatException($"无法解析 RAR 文件名: {filePath}");

        var symbol = name[..lastUnderscore];        // "cu2501" or "ag主力连续"
        var dateStr = name[(lastUnderscore + 1)..]; // "20250601"

        if (DateOnly.TryParseExact(dateStr, "yyyyMMdd", out var day))
            return (symbol, day);

        throw new FormatException($"无法解析 RAR 文件名日期: {filePath}");
    }

    /// <summary>
    /// 流式解析金数源 CSV 文件，yield 每条 Tick。
    /// Encoding: GBK (code page 936)
    /// Delimiter: comma
    /// Columns: 44 (CTP 全字段)
    /// </summary>
    public static IEnumerable<ParseResult> Parse(string filePath,
        IProgress<int>? progress = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        using var reader = new StreamReader(filePath, gbk, detectEncodingFromByteOrderMarks: false);
        var (symbol, tradingDay) = ParseFileName(filePath);
        foreach (var r in Parse(reader, symbol, tradingDay, progress))
            yield return r;
    }

    /// <summary>
    /// 从 TextReader 流式解析金数源 CSV。
    /// 用于 RAR 内嵌 CSV、内存流等场景。
    /// 调用方提供 symbol 和 tradingDay，跳过文件名解析。
    /// </summary>
    public static IEnumerable<ParseResult> Parse(TextReader reader,
        string symbol, DateOnly tradingDay, IProgress<int>? progress = null)
    {
        // 跳过表头
        var header = reader.ReadLine();
        if (header is null) yield break;

        int row = 1, good = 0, bad = 0;

        while (reader.ReadLine() is { } line)
        {
            row++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = line.Split(',');
            if (fields.Length < 26)  // 至少需要前26列 (到卖一量)
            {
                bad++;
                continue;
            }

            var result = TryParseRow(fields, symbol, tradingDay, row, ref bad);
            if (result is not null)
            {
                good++;
                yield return result.Value;
            }
        }

        progress?.Report(good);
        if (bad > 0)
            System.Diagnostics.Debug.WriteLine($"CsvTickImporter: {good} ok, {bad} skipped (of {row - 1} rows)");
    }

    private static ParseResult ParseRow(string[] f, string instrumentId, DateOnly tradingDay)
    {
        // col  1: 交易日 TradingDay
        // col  2: 合约代码 InstrumentID
        // col  5: 最新价 LastPrice
        // col 12: 数量 Volume (累计!)
        // col 13: 成交金额 Turnover (累计!)
        // col 14: 持仓量 OpenInterest
        // col 17: 涨停板价 UpperLimitPrice
        // col 18: 跌停板价 LowerLimitPrice
        // col 21: 最后修改时间 UpdateTime (HH:mm:ss)
        // col 22: 最后修改毫秒 UpdateMillisec
        // col 23: 申买价一 BidPrice1
        // col 24: 申买量一 BidVolume1
        // col 25: 申卖价一 AskPrice1
        // col 26: 申卖量一 AskVolume1

        var tradingDayStr  = f[0];
        var instId         = f[1];
        var lastPrice      = ParsePrice(f[4]);
        var volume         = long.Parse(f[11]);
        var turnover       = ParseDouble(f[12]);
        var openInterest   = ParseDouble(f[13]);
        var upperLimit     = ParseDouble(f[16]);
        var lowerLimit     = ParseDouble(f[17]);
        var updateTime     = f[20];
        var updateMs       = int.Parse(f[21]);
        var bidPrice1      = ParsePrice(f[22]);
        var bidVolume1     = int.Parse(f[23]);
        var askPrice1      = ParsePrice(f[24]);
        var askVolume1     = int.Parse(f[25]);

        // 使用文件名的交易日 (col 0 可能是前一交易日，夜盘期间)
        var day = tradingDay;
        // 也可以用 f[0] 的值作为 tradingDay（夜盘时与文件名可能不同）
        if (DateOnly.TryParseExact(tradingDayStr, "yyyyMMdd", out var csvDay))
            day = csvDay;

        // 合成 ExchangeTimestamp (Unix ms)
        // 与 C++ ParseTimestamp 行为一致: 直接按数字计算，不转换时区
        var exchangeTs = BuildTimestamp(tradingDayStr, updateTime, updateMs);

        // Flags: 涨跌停板
        var flags = 0;
        if (upperLimit > 0 && lastPrice >= (long)(upperLimit * TickRecord.PriceScale))
            flags |= TickRecord.FlagUpperLimit;
        if (lowerLimit > 0 && lastPrice <= (long)(lowerLimit * TickRecord.PriceScale))
            flags |= TickRecord.FlagLowerLimit;
        // FlagAuction: 集合竞价时段 (08:55-08:59, 20:55-20:59)
        if (updateTime is { Length: >= 5 })
        {
            var hm = updateTime[..5];
            if (hm is "08:55" or "08:56" or "08:57" or "08:58" or "08:59"
                   or "20:55" or "20:56" or "20:57" or "20:58" or "20:59")
                flags |= TickRecord.FlagAuction;
        }

        var tick = new TickRecord
        {
            ExchangeTimestamp = exchangeTs,
            LocalTimestamp    = exchangeTs,  // 历史数据，无延迟
            LastPrice         = lastPrice,
            BidPrice1         = bidPrice1,
            AskPrice1         = askPrice1,
            Volume            = volume,
            Turnover          = turnover,
            OpenInterest      = openInterest,
            BidVolume1        = bidVolume1,
            AskVolume1        = askVolume1,
            Flags             = flags,
        };

        return new ParseResult(tick, instId, day, upperLimit, lowerLimit);
    }

    /// <summary>ParseRow 的安全包装 — 捕获解析异常并报告行号</summary>
    private static ParseResult? TryParseRow(string[] f, string symbol, DateOnly tradingDay,
        int row, ref int bad)
    {
        try { return ParseRow(f, symbol, tradingDay); }
        catch
        {
            bad++;
            if (bad <= 5)
                System.Diagnostics.Debug.WriteLine($"Row {row}: parse error");
            return null;
        }
    }

    /// <summary>
    /// 从 TradingDay + UpdateTime + UpdateMillisec 构建 Unix 毫秒时间戳。
    /// 与 C++ MdSpi::ParseTimestamp 行为一致。
    /// </summary>
    private static long BuildTimestamp(string tradingDay, string updateTime, int ms)
    {
        // tradingDay: "20160111", updateTime: "20:59:00"
        if (tradingDay.Length < 8 || updateTime.Length < 8) return 0;

        int y  = int.Parse(tradingDay[..4]);
        int m  = int.Parse(tradingDay[4..6]);
        int d  = int.Parse(tradingDay[6..8]);
        int h  = int.Parse(updateTime[..2]);
        int mi = int.Parse(updateTime[3..5]);
        int s  = int.Parse(updateTime[6..8]);

        // C++ behavior: 直接按数值计算天数 + 秒，不转换时区
        // daysSinceEpoch + h*3600 + mi*60 + s → Unix seconds
        var days = DaysFromEpoch(y, m, d);
        var unixSec = days * 86400L + h * 3600L + mi * 60L + s;
        return unixSec * 1000L + ms;
    }

    /// <summary>从公历日期计算自 1970-01-01 的天数</summary>
    private static int DaysFromEpoch(int y, int m, int d)
    {
        if (m <= 2) { y--; m += 12; }
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;                          // [0, 399]
        var doy = (153 * (m - 3) + 2) / 5 + d - 1;       // [0, 365]
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy; // [0, 146096]
        return era * 146097 + doe - 719468;
    }

    private static long ParsePrice(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "0.0000") return 0;
        // CSV 格式: 35630.0000 → 35630.0 × 10^7 = 356300000000
        return (long)(double.Parse(s, CultureInfo.InvariantCulture) * TickRecord.PriceScale);
    }

    private static double ParseDouble(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        return double.Parse(s, CultureInfo.InvariantCulture);
    }
}
