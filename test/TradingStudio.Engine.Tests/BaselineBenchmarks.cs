using System.Runtime.CompilerServices;
using DuckDB.NET.Data;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;
using TradingStudio.Engine.Examples;

namespace TradingStudio.Engine.Tests;

/// <summary>
/// Phase 2 基准回测 — 用新引擎在 Top 5 流动性品种上跑 MaCross (默认参数)。
/// 旧回测数字因涨跌停/爆仓/滑点/ATR/手续费等多处修复而失效，
/// 本报告建立新基线供后续策略比较。
/// </summary>
public class BaselineBenchmarks
{
    private const long S = TickRecord.PriceScale;

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "CLAUDE.md")))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) throw new DirectoryNotFoundException("Repo root not found");
            dir = parent;
        }
        return dir;
    }

    private static string DbPath => Path.Combine(RepoRoot(), "data", "bars_history.duckdb");

    private static readonly string[] Top5 = ["rb000", "MA000", "TA000", "FG000", "SA000"];

    private sealed class MockBarFeed(List<Bar> bars) : IDataFeed
    {
        public IReadOnlyList<string> Instruments => ["rb000"];
        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }
        public void Initialize(DateTime start, DateTime end, IReadOnlyList<string> instruments)
        { StartTime = start; EndTime = end; }

        public async IAsyncEnumerable<DataEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var bar in bars)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new BarEvent { Bar = bar, Time = new DateTimeOffset(bar.BarTime, TimeSpan.FromHours(8)), IsNewBar = true };
            }
            await Task.CompletedTask;
        }
    }

    // ═══════════════════════════════════════════
    // 辅助：从 DuckDB 加载连续合约日 Bar
    // ═══════════════════════════════════════════
    private static List<Bar> LoadDailyBars(string inst)
    {
        var bars = new List<Bar>();
        using var db = new DuckDBConnection($"Data Source={DbPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT instrument_id, trading_day, bar_time, open, high, low, close, volume, turnover, open_interest, tick_count FROM bars_day WHERE instrument_id='{inst}' AND volume>0 ORDER BY bar_time";
        using var reader = (DuckDBDataReader)cmd.ExecuteReader();
        while (reader.Read())
        {
            bars.Add(new Bar
            {
                InstrumentId = inst,
                TradingDay = ReadD(reader, 1),
                BarTime = ReadT(reader, 2),
                Open = reader.GetInt64(3), High = reader.GetInt64(4), Low = reader.GetInt64(5),
                Close = reader.GetInt64(6), Volume = reader.GetInt64(7),
                Turnover = reader.GetDouble(8), OpenInterest = reader.GetDouble(9),
                TickCount = (int)reader.GetInt64(10),
            });
        }
        return bars;
    }

    private static DateOnly ReadD(DuckDBDataReader r, int i) =>
        r.GetFieldType(i) == typeof(string) ? DateOnly.Parse(r.GetString(i)) : DateOnly.FromDateTime(r.GetDateTime(i));

    private static DateTime ReadT(DuckDBDataReader r, int i) =>
        r.GetFieldType(i) == typeof(string) ? DateTime.Parse(r.GetString(i)) : r.GetDateTime(i);

    private static FutureRegistry MakeRegistry(string code)
    {
        // 精简：仅含该品种的必要字段
        return FutureRegistry.LoadFromJson($$"""
            {"symbols":[{"id":1,"exchange":"SHFE","code":"{{code}}","name":"","category":"","deliveryType":"PHYSICAL",
            "tradingUnit":10,"unitName":"","tickSize":1,"tickValue":10,"priceLimitPct":0.10,
            "marginRate":0.08,"feePerLot":5,"months":"1-12"}]}
            """);
    }

    // ═══════════════════════════════════════════
    // 基准回测
    // ═══════════════════════════════════════════

    [Fact]
    public async Task Baseline_MaCross_Top5Daily_ReportToFile()
    {
        StrategyFactory.Register<MaCrossStrategy>("MaCross");
        var outPath = Path.Combine(RepoRoot(), "scripts", "_baseline_benchmarks.txt");
        var lines = new List<string>
        {
            $"═══ MaCross 基准回测 (Top 5 流动性品种·日线) — {DateTime.Now:yyyy-MM-dd} ═══",
            "参数: 10/30 均线 无ADX 无日线趋势  风险2%  最大4手  保证金上限50%  固定费5元/手",
            "注意: 旧回测数字已全部失效(引擎多处行为变更)。本表为新基线。",
            "",
            string.Format("{0,-6} {1,10} {2,7} {3,7} {4,7} {5,6} {6,10}", "品种", "总盈亏", "胜率%", "盈亏比", "最大DD%", "交易数", "终值"),
            new string('-', 65),
        };

        foreach (var inst in Top5)
        {
            var code = inst.Replace("000", "");
            var bars = LoadDailyBars(inst);
            // 打印第一条和最后一条 bar 确认数据加载
            Console.WriteLine($"{code}: loaded {bars.Count} bars, first={bars[0].BarTime:yyyy-MM-dd}, last={bars[^1].BarTime:yyyy-MM-dd}, open={bars[0].OpenDouble:F0}");
            if (bars.Count < 500) { lines.Add($"{code,-6}  (不足500根日Bar,跳过)"); continue; }

            var feed = new MockBarFeed(bars);
            feed.Initialize(bars[0].BarTime, bars[^1].BarTime, [inst]);

            var config = new StrategyConfig
            {
                StrategyId = "baseline", StrategyType = "MaCross",
                Instruments = [inst], Priority = 1, AllocatedCapital = 100_000m,
            };
            config.Parameters.Add("FastPeriod", "10");
            config.Parameters.Add("SlowPeriod", "30");
            config.Parameters.Add("AdxPeriod", "0");        // disable ADX filter
            config.Parameters.Add("MinAdx", "0");
            config.Parameters.Add("DailyTrendFilter", "false");
            config.Parameters.Add("MaxPosition", "4");
            config.Parameters.Add("MaxMarginRatio", "0.50");  // allow up to 50% margin
            config.Parameters.Add("RiskPerTrade", "0.02");
            var options = new EngineOptions
            {
                StartTime = bars[0].BarTime, EndTime = bars[^1].BarTime,
                Instruments = [inst], StrategyConfigs = [config],
                StartingCapital = 100_000, IsLive = false, WarmupDays = 30,
            };

            try
            {
                var engine = new TradingEngine(
                    feed, new ExecutionHandler(new RiskController(maxDrawdown: 1.0m)),
                    new PortfolioManager(100_000), new IndicatorManager(),
                    new StrategyContainer(), new RiskController(maxDrawdown: 1.0m),
                    new FeedbackMonitor(), new TickSnapshot(), options, MakeRegistry(code));

                var report = await engine.RunAsync(CancellationToken.None);
                var sr = report.StrategyReports.First();

                var totalPnL = sr.TotalNetProfit;
                var wr = sr.TotalTrades > 0 ? (double)sr.WinRate * 100 : 0;
                var plr = sr.ProfitLossRatio;
                var mdd = sr.MaxDrawdown;
                var trades = sr.TotalTrades;
                var finalEquity = report.FinalPortfolio.TotalEquity;

                lines.Add($"{code,-6} {totalPnL,10:F0} {wr,6:F1}% {plr,7:F2} {mdd,7:F2} {trades,6} {finalEquity,10:F0}");
            }
            catch (Exception ex)
            {
                lines.Add($"{code,-6}  ERROR: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            }
        }

        File.WriteAllLines(outPath, lines);
        Console.WriteLine($"Benchmark done → {outPath}");
    }
}
