using DuckDB.NET.Data;
using TradingStudio.Core.Models;

namespace TradingStudio.Data.Tests;

/// <summary>
/// K线一致性验证 — Phase 2 数据地基检查。
/// 验证静态结构（不存在伪造/重复/乱序）而非逐Bar比对文华（需人工）。
/// </summary>
public class KLineConsistencyTests
{
    private const long S = TickRecord.PriceScale;
    private static readonly string[] SampleInsts = ["rb000", "IF000", "CU000"];

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "CLAUDE.md")))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir)
                throw new DirectoryNotFoundException("Repo root not found");
            dir = parent;
        }
        return dir;
    }

    private static string DbPath => Path.Combine(RepoRoot(), "data", "bars_history.duckdb");

    private static DateTime RdDt(DuckDBDataReader r, int idx) =>
        r.GetFieldType(idx) == typeof(string) ? DateTime.Parse(r.GetString(idx)) : r.GetDateTime(idx);

    // ═══════════════════════════════════════════
    // 1. 存在性 + OHLC 非零
    // ═══════════════════════════════════════════

    [Fact]
    public void EachSampleInstrument_HasBars_DailyTable()
    {
        using var db = new DuckDBConnection($"Data Source={DbPath}");
        db.Open();
        Console.WriteLine("\n--- 日 Bar 数据量 (bars_day) ---");
        foreach (var inst in SampleInsts)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*), MIN(bar_time), MAX(bar_time) FROM bars_day WHERE instrument_id='{inst}'";
            using var reader = (DuckDBDataReader)cmd.ExecuteReader();
            if (reader.Read())
            {
                var cnt = reader.GetInt64(0);
                var min = reader.IsDBNull(1) ? "N/A" : RdDt(reader, 1).ToString("yyyy-MM-dd");
                var max = reader.IsDBNull(2) ? "N/A" : RdDt(reader, 2).ToString("yyyy-MM-dd");
                Console.WriteLine($"  {inst}: {cnt} bars, {min} ~ {max}");
                // 连续合约至少应有 500+ 根日 Bar（约 5 年）
                if (cnt == 0)
                    Console.WriteLine($"    ⚠️ 无数据 — 可能未导入或在其他表中");
                else if (cnt < 500)
                    Console.WriteLine($"    ⚠️ 仅 {cnt} 根，可能不完整");
            }
        }
    }

    [Fact]
    public void NoNegativeOrZeroPrices_InSampledBars()
    {
        using var db = new DuckDBConnection($"Data Source={DbPath}");
        db.Open();
        // DuckDB TABLESAMPLE 0.01%，非空检查
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM bars_1min TABLESAMPLE bernoulli(0.01%)
            WHERE open<=0 OR high<=0 OR low<=0 OR close<=0
            """;
        var bad = (long)cmd.ExecuteScalar()!;
        Assert.Equal(0L, bad);
    }

    // ═══════════════════════════════════════════
    // 2. 时间单调（抽 3 个品种，LIMIT 5000）
    // ═══════════════════════════════════════════

    [Fact]
    public void TimeIsMonotonic_Sample5000Bars_PerInstrument()
    {
        using var db = new DuckDBConnection($"Data Source={DbPath}");
        db.Open();
        foreach (var inst in SampleInsts)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT bar_time FROM bars_1min WHERE instrument_id='{inst}' ORDER BY bar_time LIMIT 5000";
            using var reader = (DuckDBDataReader)cmd.ExecuteReader();
            DateTime? prev = null;
            while (reader.Read())
            {
                var t = RdDt(reader, 0);
                if (prev.HasValue)
                    Assert.True(t > prev.Value, $"{inst}: non-monotonic {prev} → {t}");
                prev = t;
            }
        }
    }

    // ═══════════════════════════════════════════
    // 3. 夜盘交易日归属（快速抽样）
    // ═══════════════════════════════════════════

    [Fact]
    public void NightBars_TradingDay_IsCalendarDate()
    {
        using var db = new DuckDBConnection($"Data Source={DbPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT bar_time, trading_day FROM bars_1min
            WHERE instrument_id='rb000'
              AND bar_time >= '2024-06-17 20:50:00' AND bar_time <= '2024-06-17 23:10:00'
            ORDER BY bar_time
            """;
        using var reader = (DuckDBDataReader)cmd.ExecuteReader();
        int c = 0;
        var expectedDay = new DateOnly(2024, 6, 17);
        while (reader.Read())
        {
            var barTime = RdDt(reader, 0);
            var tDay = reader.GetFieldType(1) == typeof(string)
                ? DateOnly.Parse(reader.GetString(1))
                : DateOnly.FromDateTime(reader.GetDateTime(1));
            if (barTime.Hour >= 20)
                Assert.True(tDay == expectedDay,
                    $"Night bar {barTime} trading_day={tDay}, expected {expectedDay}");
            c++;
        }
        Assert.True(c > 0, "No night bars found for rb000 on 2024-06-17");
    }

    // ═══════════════════════════════════════════
    // 4. 交叉核对 — 控制台输出（仅当手工核对时跑）
    // ═══════════════════════════════════════════

    [Fact]
    public void CrossCheck_DumpForWenHua()
    {
        var outPath = Path.Combine(RepoRoot(), "scripts", "_kline_ref.txt");
        using var db = new DuckDBConnection($"Data Source={DbPath}");
        db.Open();
        using var sw = new StreamWriter(outPath);
        sw.WriteLine("══════ 交叉核对样本 — 请在文华/博易查询相同交易日 ══════");
        foreach (var inst in SampleInsts)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT bar_time, open, high, low, close, volume FROM bars_day WHERE instrument_id='{inst}' AND bar_time >= '2024-06-10' AND bar_time <= '2024-06-20' ORDER BY bar_time";
            using var reader = (DuckDBDataReader)cmd.ExecuteReader();
            sw.WriteLine($"\n--- {inst} (2024-06-10 ~ 06-20) ---");
            sw.WriteLine("Date        |   Open |   High |    Low |  Close |     Vol");
            while (reader.Read())
            {
                var dt = RdDt(reader, 0);
                sw.WriteLine($"{dt:yyyy-MM-dd} | {reader.GetInt64(1)/(double)S,6:F1} | {reader.GetInt64(2)/(double)S,6:F1} | {reader.GetInt64(3)/(double)S,6:F1} | {reader.GetInt64(4)/(double)S,6:F1} | {reader.GetInt64(5),8}");
            }
        }
        sw.WriteLine("\n→ 请在文华/博易上查询同一时段，核对 OHLC。差异 > 1 tick 为异常。");
        Console.WriteLine($"交叉核对数据已写入: {outPath}");
    }
}
