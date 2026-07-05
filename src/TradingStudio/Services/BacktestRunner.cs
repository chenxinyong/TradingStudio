using System.Collections.Concurrent;
using System.Text.Json;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Data.Engine;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;
using TradingStudio.Engine.Statistics;

namespace TradingStudio.Services;

/// <summary>
/// 后台回测执行器 — 在 live 模式下异步运行回测，不阻塞实盘引擎。
/// </summary>
public class BacktestRunner
{
    private readonly ConcurrentDictionary<string, BacktestTask> _tasks = new();
    private readonly IServiceProvider _sp;
    private readonly ILogger<BacktestRunner> _log;
    private int _counter;

    public BacktestRunner(IServiceProvider sp, ILogger<BacktestRunner> log)
    {
        _sp = sp; _log = log;
    }

    /// <summary>启动一个回测任务，返回任务ID</summary>
    public string Start(StrategyConfig config, string? dbPath = null, string? symbolsPath = null)
    {
        var id = $"bt_{Interlocked.Increment(ref _counter):D4}";
        var task = new BacktestTask { Id = id, Config = config, Status = "pending", StartedAt = DateTime.Now };
        _tasks[id] = task;

        _ = Task.Run(() => RunBacktest(id, config, dbPath, symbolsPath));
        _log.LogInformation("Backtest {Id} started: {Strategy} on [{Instruments}]",
            id, config.StrategyType, string.Join(",", config.Instruments));
        return id;
    }

    public BacktestTask? Get(string id) => _tasks.TryGetValue(id, out var t) ? t : null;

    public IEnumerable<BacktestTask> All => _tasks.Values.OrderByDescending(t => t.StartedAt).Take(50);

    private async Task RunBacktest(string id, StrategyConfig config, string? dbPath, string? symbolsPath)
    {
        var task = _tasks[id];
        task.Status = "running";
        try
        {
            var report = await Task.Run(() => ExecuteBacktest(config, dbPath, symbolsPath));
            task.ReportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new EquityCurveJsonConverter() },
            });
            task.Status = "completed";
            task.CompletedAt = DateTime.Now;
            task.Summary = BuildSummary(report);
            _log.LogInformation("Backtest {Id} completed: {PnL}", id, report.TotalReturn);
        }
        catch (Exception ex)
        {
            task.Status = "failed";
            task.Error = ex.ToString();
            _log.LogError(ex, "Backtest {Id} failed", id);
        }
    }

    private EngineReport ExecuteBacktest(StrategyConfig config, string? dbPath, string? symbolsPath)
    {
        var db = dbPath ?? "data/bars_history.duckdb";
        var sym = symbolsPath ?? "symbols.json";

        var registry = FutureRegistry.Load(sym);
        StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);
        StrategyFactory.DiscoverFromAssembly(typeof(TradingStudio.Strategy.ChanLunStrategy).Assembly);

        var risk = new RiskController(
            maxPosition: 5, maxDrawdown: 0.25m, maxStrategyDrawdown: 0.25m);
        var feedback = new FeedbackMonitor();
        var tickSnapshot = new TickSnapshot();
        var execution = new ExecutionHandler(risk);
        var indicators = new IndicatorManager();
        var strategies = new StrategyContainer();

        var capital = config.AllocatedCapital > 0 ? config.AllocatedCapital : 100_000m;
        var portfolio = new PortfolioManager(capital);

        var start = config.DataStartDate ?? new DateTime(2021, 1, 1);
        var end = config.DataEndDate ?? new DateTime(2026, 6, 30);

        var warmupDays = Math.Max((int)(end - start).TotalDays + 10, 365);

        var options = new EngineOptions
        {
            StartTime = start, EndTime = end,
            Instruments = config.Instruments,
            StrategyConfigs = [config],
            StartingCapital = capital,
            WarmupDays = warmupDays,
            SkipAuction = config.SkipAuction,
        };

        // Historical data feed
        var period = config.BarPeriodMinutes > 0 ? config.BarPeriodMinutes : 1;
        var table = !string.IsNullOrEmpty(config.PrimaryBarType) ? config.PrimaryBarType : "bars_1min";
        var store = new DuckDBStore(db, readOnly: true);
        var dataFeed = new HistoricalBarFeed(store, period, table);

        var engine = new TradingEngine(
            dataFeed, execution, portfolio, indicators, strategies,
            risk, feedback, tickSnapshot, options, registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TradingStudio.Engine.TradingEngine>.Instance);

        return engine.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static BacktestSummary BuildSummary(EngineReport report)
    {
        var sr = report.StrategyReports.FirstOrDefault();
        if (sr == null) return new BacktestSummary();
        return new BacktestSummary
        {
            StrategyId = sr.StrategyId,
            TotalTrades = sr.TotalTrades,
            TotalNetProfit = sr.TotalNetProfit,
            WinRate = sr.WinRate,
            ProfitLossRatio = sr.ProfitLossRatio,
            MaxDrawdown = sr.MaxDrawdown,
            SharpeRatio = sr.SharpeRatio,
            CompoundingAnnualReturn = sr.CompoundingAnnualReturn,
        };
    }
}

public class BacktestTask
{
    public string Id { get; init; } = "";
    public StrategyConfig? Config { get; init; }
    public string Status { get; set; } = "pending"; // pending/running/completed/failed
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public string? ReportJson { get; set; }
    public string? Error { get; set; }
    public BacktestSummary? Summary { get; set; }
}

public class BacktestSummary
{
    public string StrategyId { get; set; } = "";
    public int TotalTrades { get; set; }
    public decimal TotalNetProfit { get; set; }
    public decimal WinRate { get; set; }
    public decimal ProfitLossRatio { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal SharpeRatio { get; set; }
    public decimal CompoundingAnnualReturn { get; set; }
}
