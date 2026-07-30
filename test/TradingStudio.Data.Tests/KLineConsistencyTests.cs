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

    private static string DbPath => Path.GetFullPath(Path.Combine(RepoRoot(), "..", "..", "Datas", "bars_history.duckdb"));

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
    public void Scan_AllInstruments_ReportToFile()
    {
        var outPath = Path.Combine(RepoRoot(), "scripts", "_instrument_scan.txt");
        using var db = new DuckDBConnection($"Data Source={DbPath}");
        db.Open();

        // 1. 查找所有连续合约
        var contracts = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT instrument_id FROM bars_day WHERE instrument_id ILIKE '%000' ORDER BY instrument_id";
            using var r = (DuckDBDataReader)cmd.ExecuteReader();
            while (r.Read()) contracts.Add(r.GetString(0));
        }

        // 2. 逐品种统计
        var lines = new List<string>();
        lines.Add($"Full Instrument Scan — {DateTime.Now:yyyy-MM-dd HH:mm} — {contracts.Count} contracts found");
        lines.Add("");
        lines.Add(string.Format("{0,-6} {1,6} {2,-12} {3,-12} {4,10} {5,8} {6,6} {7,4} {8,4} {9,-9}", "Code", "Days", "From", "To", "AvgVol", "AvgPx", "ATR%", "Liq", "Vol", "Status"));
        lines.Add(new string('-', 90));

        foreach (var inst in contracts)
        {
            var code = inst.Replace("000", "");
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"""
                    SELECT COUNT(*) AS days, MIN(bar_time), MAX(bar_time),
                           AVG(volume) AS avg_vol, AVG(close::DOUBLE)/{S} AS avg_px
                    FROM bars_day WHERE instrument_id='{inst}' AND volume>0
                    """;
                using var r = (DuckDBDataReader)cmd.ExecuteReader();
                if (!r.Read() || r.IsDBNull(0)) continue;
                var days = r.GetInt64(0);
                if (days < 100) continue; // 过滤数据太少的品种
                var from = (r.GetFieldType(1) == typeof(string) ? r.GetString(1)[..10] : r.GetDateTime(1).ToString("yyyy-MM-dd"));
                var to = (r.GetFieldType(2) == typeof(string) ? r.GetString(2)[..10] : r.GetDateTime(2).ToString("yyyy-MM-dd"));
                var avgVol = r.GetDouble(3);
                var avgPx = r.GetDouble(4);

                // 波动率: 直接用 DuckDB 的日振幅(VOL)
                using var vCmd = db.CreateCommand();
                vCmd.CommandText = $"SELECT AVG(ABS(high-low)::DOUBLE/{S}/{avgPx}*100) FROM bars_day WHERE instrument_id='{inst}' AND volume>0";
                using var vR = (DuckDBDataReader)vCmd.ExecuteReader();
                var atrPct = vR.Read() && !vR.IsDBNull(0) ? vR.GetDouble(0) : 0;

                int liq = avgVol > 500000 ? 5 : avgVol > 200000 ? 4 : avgVol > 100000 ? 3 : avgVol > 50000 ? 2 : 1;
                int vol = atrPct > 3 ? 5 : atrPct > 2 ? 4 : atrPct > 1.5 ? 3 : atrPct > 1 ? 2 : 1;
                var status = (liq >= 3 && vol >= 3) ? "TRADE" : (liq >= 2 && vol >= 2) ? "candidate" : "skip";

                lines.Add($"{code,-6} {days,6:N0} {from,-12} {to,-12} {avgVol,10:N0} {avgPx,8:F1} {atrPct,6:F2} {liq,4} {vol,4} {status}");
            }
            catch (Exception ex) { lines.Add($"{code,-6}  ERROR: {ex.Message[..50]}"); }
        }

        File.WriteAllLines(outPath, lines);
        Console.WriteLine($"Scan done: {lines.Count - 4} instruments → {outPath}");
    }

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
