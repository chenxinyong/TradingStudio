using Microsoft.Data.Sqlite;

namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 缠论算法验证器 — 从 TradingStudio SQLite 数据库加载数据并验证分析结果。
/// </summary>
public static class ChanLunValidator
{
    private const double PriceScale = 10_000_000;

    /// <summary>
    /// 从 SQLite 数据库加载 1min Bar 并聚合到指定周期。
    /// </summary>
    public static List<ChanLunBar> LoadBars(
        string dbPath,
        string instrumentId,
        int periodMinutes = 15,
        int? limit = null)
    {
        var oneMinBars = new List<ChanLunBar>();

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var sql = @"SELECT bar_time, open, high, low, close, volume
                    FROM bars_1min
                    WHERE instrument_id = @inst
                    ORDER BY bar_time ASC";
        if (limit.HasValue)
            sql += $" LIMIT {limit.Value}";

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@inst", instrumentId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            oneMinBars.Add(new ChanLunBar
            {
                Dt = DateTime.Parse(reader.GetString(0)),
                Open = reader.GetInt64(1) / PriceScale,
                High = reader.GetInt64(2) / PriceScale,
                Low = reader.GetInt64(3) / PriceScale,
                Close = reader.GetInt64(4) / PriceScale,
                Volume = reader.GetInt64(5),
            });
        }

        if (oneMinBars.Count == 0 || periodMinutes == 1)
            return oneMinBars.OrderBy(b => b.Dt).ToList();

        // 使用 Floor 对齐（与 pandas resample 一致）
        return ResampleBars(oneMinBars, periodMinutes);
    }

    /// <summary>
    /// 聚合到指定周期。使用 Floor 分组（与 pandas resample 一致）。
    /// </summary>
    private static List<ChanLunBar> ResampleBars(List<ChanLunBar> bars, int periodMinutes)
    {
        static DateTime FloorToPeriod(DateTime dt, int periodMin)
        {
            int totalMinutes = dt.Hour * 60 + dt.Minute;
            int floored = totalMinutes / periodMin * periodMin;
            return new DateTime(dt.Year, dt.Month, dt.Day, floored / 60, floored % 60, 0);
        }

        var groups = bars.GroupBy(b => FloorToPeriod(b.Dt, periodMinutes));
        var result = new List<ChanLunBar>();

        foreach (var g in groups)
        {
            var groupBars = g.OrderBy(b => b.Dt).ToList();
            result.Add(new ChanLunBar
            {
                Dt = g.Key,
                Open = groupBars[0].Open,
                High = groupBars.Max(b => b.High),
                Low = groupBars.Min(b => b.Low),
                Close = groupBars[^1].Close,
                Volume = groupBars.Sum(b => b.Volume),
            });
        }

        return result.OrderBy(b => b.Dt).ToList();
    }

    /// <summary>
    /// 运行完整验证: 加载数据 → 分析 → 打印结果。
    /// </summary>
    public static ChanLunResult RunValidation(string dbPath, string instrumentId, int minBiLen = 5)
    {
        Console.WriteLine("=".PadRight(60, '='));
        Console.WriteLine($"缠论 C# 算法验证 — {instrumentId} (15min, MIN_BI_LEN={minBiLen})");
        Console.WriteLine("=".PadRight(60, '='));

        // Load
        var bars = LoadBars(dbPath, instrumentId, periodMinutes: 15);
        Console.WriteLine($"\n[Load] {bars.Count} 根15min K线  [{bars[0].Dt} → {bars[^1].Dt}]");

        // Analyze
        var result = ChanLunAnalyzer.Analyze(bars, minBiLen: minBiLen);
        Console.WriteLine($"[Analyze] 标准K线={result.StdCount}  分型={result.FractalCount}  笔={result.BiCount}  中枢={result.ZhongshuCount}  走势={result.Trend}");

        // Validate
        var (inclOk, fxOk, biOk) = ChanLunAnalyzer.Validate(result, minBiLen);
        Console.WriteLine($"[Validate] 包含处理={(inclOk ? "PASS" : "FAIL")}  分型={(fxOk ? "PASS" : "FAIL")}  笔={(biOk ? "PASS" : "FAIL")}");

        // BI stats
        if (result.Bis.Count > 0)
        {
            var upBis = result.Bis.Where(b => b.Type == Direction.Up).ToList();
            var dnBis = result.Bis.Where(b => b.Type == Direction.Down).ToList();

            foreach (var (label, bis) in new[] { ("UP", upBis), ("DOWN", dnBis) })
            {
                if (bis.Count == 0) continue;
                var powers = bis.Select(b => b.Power).ToList();
                var lengths = bis.Select(b => (double)b.BarCount).ToList();
                Console.WriteLine($"  {label}: {bis.Count}笔  power avg={powers.Average():F1} max={powers.Max():F1}  len avg={lengths.Average():F1} max={lengths.Max():F0}");
            }
        }

        // ZS
        Console.WriteLine($"\n  中枢: {result.ZhongshuCount}个");
        foreach (var zs in result.Zhongshus.Take(5))
            Console.WriteLine($"    [{zs.Zd:F1}, {zs.Zg:F1}] zz={zs.Zz:F1}  {zs.DtStart}");

        // First 10 bis
        Console.WriteLine($"\n  前10笔:");
        foreach (var bi in result.Bis.Take(10))
        {
            var arrow = bi.Type == Direction.Up ? "UP" : "DN";
            Console.WriteLine($"    [{arrow}] len={bi.BarCount}K power={bi.Power:F1} change={bi.ChangePct:F2}% {bi.DtStart:yyyy-MM-dd HH:mm}→{bi.DtEnd:yyyy-MM-dd HH:mm}");
        }

        return result;
    }
}
