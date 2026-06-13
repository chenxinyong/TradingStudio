using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using TradingStudio.Data.Storage;

namespace TradingStudio.Data.Import;

/// <summary>
/// Tick 导入服务 — 编排 CSV 解析 → Bar 聚合 → SQLite 存储的完整管线。
/// </summary>
public class TickImportService
{
    /// <summary>
    /// 导入单个金数源 CSV 文件，生成 1min + Day Bar 并写入 SQLite。
    /// </summary>
    /// <param name="filePath">CSV 文件路径</param>
    /// <param name="dbPath">SQLite 数据库路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>导入统计</returns>
    public static async Task<ImportStats> ImportFileAsync(
        string filePath, string dbPath = "bars.db", CancellationToken ct = default)
    {
        var (symbol, tradingDay) = CsvTickImporter.ParseFileName(filePath);
        Console.WriteLine($"📄 {symbol}  {tradingDay:yyyy-MM-dd}  ← {Path.GetFileName(filePath)}");

        // 1. 聚合器
        using var barAgg = new BarAggregator();
        using var dayAgg = new DailyBarAggregator();

        var bars = new List<Bar>(4096);
        barAgg.OnBar += b => { lock (bars) bars.Add(b); };
        dayAgg.OnBar += b => { lock (bars) bars.Add(b); };

        // 2. 流式解析 + 喂入聚合器
        int tickCount = 0;
        var lastReport = DateTime.UtcNow;

        foreach (var r in CsvTickImporter.Parse(filePath))
        {
            if (ct.IsCancellationRequested) break;

            barAgg.Feed(r.Tick, r.InstrumentId, r.TradingDay);
            dayAgg.Feed(r.Tick, r.InstrumentId, r.TradingDay);
            tickCount++;

            // 每秒报告进度
            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalSeconds >= 1)
            {
                Console.Write($"\r  Ticks: {tickCount:N0}  Bars: {bars.Count:N0}  ");
                lastReport = now;
            }
        }

        // 3. Flush 聚合器
        barAgg.Flush();
        dayAgg.FlushAll();

        Console.Write($"\r  Ticks: {tickCount:N0}  Bars: {bars.Count:N0}  ");
        Console.WriteLine();

        if (bars.Count == 0)
            return new ImportStats(symbol, tradingDay, tickCount, 0, 0);

        // 4. 写入 SQLite
        var minBars = bars.Count(b => b.BarTime.TimeOfDay != TimeSpan.Zero);
        var dayBars = bars.Count(b => b.BarTime.TimeOfDay == TimeSpan.Zero);

        Console.Write($"  Writing {bars.Count:N0} bars to {dbPath}... ");
        using var barStore = new BarStore(dbPath);
        await barStore.WriteBatchAsync(bars, ct);
        Console.WriteLine($"Done. (1min: {minBars}, day: {dayBars})");

        return new ImportStats(symbol, tradingDay, tickCount, minBars, dayBars);
    }

    /// <summary>
    /// 批量导入目录下所有金数源 CSV 文件。
    /// </summary>
    public static async Task<List<ImportStats>> ImportDirectoryAsync(
        string dirPath, string dbPath = "bars.db",
        string searchPattern = "金数源_*_CTP格式.csv",
        CancellationToken ct = default)
    {
        var files = Directory.GetFiles(dirPath, searchPattern, SearchOption.TopDirectoryOnly);
        Array.Sort(files); // 按文件名排序（含日期）

        Console.WriteLine($"Found {files.Length} CSV files in {dirPath}\n");

        var allStats = new List<ImportStats>();
        int totalTicks = 0, totalMin = 0, totalDay = 0;

        foreach (var (file, i) in files.Select((f, i) => (f, i)))
        {
            if (ct.IsCancellationRequested) break;

            Console.WriteLine($"[{i + 1}/{files.Length}] ", Path.GetFileName(file));
            var stats = await ImportFileAsync(file, dbPath, ct);
            allStats.Add(stats);

            totalTicks += stats.TickCount;
            totalMin   += stats.MinuteBars;
            totalDay   += stats.DayBars;

            Console.WriteLine();
        }

        Console.WriteLine("═══════════════════════════════════");
        Console.WriteLine($"  Total: {files.Length} files, {totalTicks:N0} ticks, {totalMin:N0} 1min bars, {totalDay} day bars");
        Console.WriteLine("═══════════════════════════════════");

        return allStats;
    }
}

/// <summary>单文件导入统计</summary>
public readonly record struct ImportStats(
    string Symbol,
    DateOnly TradingDay,
    int TickCount,
    int MinuteBars,
    int DayBars);
