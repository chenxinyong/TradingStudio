// 全品种扫描 — 生成可回测品种清单
// 编译并运行:
//   dotnet run --project scripts/InstrumentScan.csproj
// 输出: scripts/_instrument_scan.txt
using DuckDB.NET.Data;

class Scan
{
    const string DbPath = "data/bars_history.duckdb";
    const long S = 10_000_000;
    const int MinDays = 200; // 最少交易日数

    static void Main()
    {
        var report = new List<string>();
        report.Add("══════════════════════════════════════════════════════════════════════════");
        report.Add("  全品种扫描报告 — TradingStudio 连续合约数据检查");
        report.Add($"  生成时间: {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.Add("══════════════════════════════════════════════════════════════════════════");
        report.Add("");
        report.Add($"{"品种",-6} {"名称",-8} {"Bar数",>6} {"起始",-12} {"截止",-12} {"日均量",>10} {"年均ATR%",>8} {"波动分",>6} {"流动性分",>6} {"状态",-6}");
        report.Add(new string('-', 100));

        using var db = new DuckDBConnection($"Data Source={DbPath}");
        db.Open();

        // 1. 找出所有连续合约 (xxx000)
        var contracts = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT instrument_id FROM bars_day WHERE instrument_id ILIKE '%000' ORDER BY instrument_id";
            using var r = (DuckDBDataReader)cmd.ExecuteReader();
            while (r.Read()) contracts.Add(r.GetString(0));
        }

        Console.WriteLine($"Found {contracts.Count} continuous contracts");

        // 2. 逐品种统计
        foreach (var inst in contracts)
        {
            var code = inst.Replace("000", "");
            try
            {
                using var cmd = db.CreateCommand();

                // 聚合: 从 bars_day 取得逐日 OHLCV
                cmd.CommandText = $@"
                    SELECT COUNT(*) AS days,
                           MIN(bar_time), MAX(bar_time),
                           AVG(close::DOUBLE)/{S} AS avg_close,
                           AVG(volume) AS avg_vol,
                           STDDEV((close-open)::DOUBLE/open) AS atr_pct
                    FROM bars_day WHERE instrument_id='{inst}' AND volume>0";
                using var r = (DuckDBDataReader)cmd.ExecuteReader();
                if (!r.Read() || r.IsDBNull(0)) continue;

                var days = r.GetInt64(0);
                if (days < MinDays) continue;

                var minDate = r.GetString(1)[..10];
                var maxDate = r.GetString(2)[..10];
                var avgPrice = r.GetDouble(3);
                var avgVol = r.GetDouble(4);
                var atrPct = r.IsDBNull(5) ? 0 : r.GetDouble(5) / avgPrice * 100; // 年化 ATR%

                // Simple scores
                var liqScore = avgVol > 500000 ? 5 : avgVol > 200000 ? 4 : avgVol > 100000 ? 3 : avgVol > 50000 ? 2 : 1;
                var volScore = atrPct > 3 ? 5 : atrPct > 2 ? 4 : atrPct > 1.5 ? 3 : atrPct > 1 ? 2 : 1;
                var status = (liqScore >= 3 && volScore >= 3) ? "可交易" : (liqScore >= 2 && volScore >= 2) ? "候选" : "过滤";

                report.Add($"{code,-6} {code,-8} {days,6:N0} {minDate,-12} {maxDate,-12} {avgVol,10:N0} {atrPct,8:F2} {volScore,6} {liqScore,6} {status,-6}");
            }
            catch { /* skip on error */ }
        }

        // 3. 写入文件
        var outPath = "scripts/_instrument_scan.txt";
        File.WriteAllLines(outPath, report);
        Console.WriteLine($"\nReport written to {outPath} ({report.Count - 8} instruments)");
    }
}
