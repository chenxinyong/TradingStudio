using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;
using TradingStudio.Live;

namespace TradingStudio.Services;

/// <summary>
/// Live 模式 DI 组合根 — 从 Program.cs 提取，保持入口文件简洁。
/// </summary>
public static class LiveComposer
{
    private static readonly List<string> _pendingStrategyInstruments = new();

    public static void Configure(WebApplicationBuilder builder, IConfiguration config)
    {
        var services = builder.Services;

        // ── 基础设施 ──
        services.AddSingleton<SessionScheduler>();
        services.AddSingleton<HealthMonitor>();

        // ── 品种注册表 ──
        var symbolsPath = config["Live:SymbolsPath"] ?? "symbols.json";
        var registry = FutureRegistry.Load(symbolsPath);
        services.AddSingleton(registry);

        // ── CTP 行情 ──
        var mdOpts = new CtpMdOptions
        {
            MdFront = config["Live:MdFront"]!,
            BrokerId = config["Live:BrokerId"] ?? "9999",
            UserId = config["Live:UserId"]!,
            Password = config["Live:Password"]!,
        };
        var activityTracker = new ContractActivityTracker(observationSeconds: 60);
        services.AddSingleton(activityTracker);
        services.AddSingleton<IDataFeed>(sp =>
        {
            var feed = new CtpLiveFeedV2(mdOpts, sp.GetRequiredService<Serilog.ILogger>());
            feed.ActivityTracker = activityTracker;
            foreach (var inst in _pendingStrategyInstruments) feed.StrategyInstruments.Add(inst);
            return feed;
        });
        services.AddSingleton(sp => (CtpLiveFeedV2)sp.GetRequiredService<IDataFeed>());

        // ── 风控 ──
        var risk = new RiskController(
            maxPosition: config.GetValue("Risk:MaxPositionPerInstrument", 5),
            maxOrderQty: config.GetValue("Risk:MaxOrderQuantity", 100),
            maxDrawdown: config.GetValue<decimal>("Risk:MaxDrawdownPct", 0.25m));
        services.AddSingleton(risk);
        var execution = new ExecutionHandler(risk, registry);
        services.AddSingleton<IExecutionHandler>(execution);
        services.AddSingleton(execution);

        // ── 反馈 + 行情快照 ──
        services.AddSingleton(new FeedbackMonitor());
        services.AddSingleton(new TickSnapshot());

        // ── 指标 + 策略 ──
        services.AddSingleton(new IndicatorManager());
        services.AddSingleton(new StrategyContainer());

        // ── 回测执行器 ──
        services.AddSingleton<BacktestRunner>();

        // ── 数据持久化 ──
        var dataPath = config["Live:DataPath"] ?? "data";
        var dbPath = Path.Combine(dataPath, config["Live:Database"] ?? "bars_live.db");
        var forceDuckDB = config["Live:UseDuckDB"]?.ToLowerInvariant() == "true";
        var isDuckDB = forceDuckDB || dbPath.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase);
        IBarStore barStore = isDuckDB
            ? new DuckDBStore(dbPath, enableTickPurge: true)
            : new SqliteBarStore(dbPath);
        services.AddSingleton(barStore);
        services.AddSingleton<BuildPeriodsService>();
        services.AddSingleton(new TickCsvWriter(Path.Combine(dataPath, "TickData")));

        // ── 资金管理 ──
        var startCapital = config.GetValue<decimal>("Live:StartingCapital", 100_000m);
        var portfolio = new PortfolioManager(startCapital);
        services.AddSingleton(portfolio);

        // ── CTP 交易桥接 ──
        if (!string.IsNullOrEmpty(config["Live:TraderFront"]))
        {
            var traderOpts = new CtpTraderOptions
            {
                TraderFront = config["Live:TraderFront"]!,
                BrokerId = config["Live:BrokerId"] ?? "9999",
                UserId = config["Live:UserId"] ?? "",
                Password = config["Live:Password"] ?? "",
                AuthCode = config["Live:AuthCode"] ?? "0000000000000000",
                AppId = config["Live:AppId"] ?? "simnow_client_test",
            };
            var bridgeLogger = builder.Services.BuildServiceProvider().GetRequiredService<Serilog.ILogger>();
            var bridge = new CtpTraderBridgeV2(execution.FillChannel, traderOpts, bridgeLogger);
            services.AddSingleton(bridge);
            try
            {
                bridge.Connect();
                Console.Error.WriteLine("[LiveComposer] CtpTraderBridgeV2.Connect() called — FtdcNet.CTP P/Invoke");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LiveComposer] CtpTraderBridge.Connect() FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            execution.SendToExchange = bridge.SendOrder;
        }
        execution.IsLive = true;

        // ── 引擎选项 + 策略加载 ──
        var allInstruments = ContractCodeGenerator.BatchSubscribe(registry.All.Values, 50)
            .SelectMany(b => b).ToList();
        var engineOptions = new EngineOptions
        {
            StartTime = DateTime.Today, EndTime = DateTime.Today.AddDays(1),
            Instruments = allInstruments, StartingCapital = startCapital, IsLive = true,
        };

        var strategyConfigPath = config["Live:StrategyConfig"];
        // 相对路径 → 从 EXE 目录解析（CWD 可能不是 EXE 目录）
        if (!string.IsNullOrEmpty(strategyConfigPath))
        {
            if (!Path.IsPathRooted(strategyConfigPath))
                strategyConfigPath = Path.Combine(AppContext.BaseDirectory, strategyConfigPath);
            Console.Error.WriteLine($"[LiveComposer] Strategy config path: {strategyConfigPath}  exists={File.Exists(strategyConfigPath)}");
        }
        if (!string.IsNullOrEmpty(strategyConfigPath) && File.Exists(strategyConfigPath))
        {
            var json = File.ReadAllText(strategyConfigPath);
            TradingStudio.Core.Strategy.StrategyConfig? sc = null;
            try
            {
                sc = System.Text.Json.JsonSerializer.Deserialize<TradingStudio.Core.Strategy.StrategyConfig>(
                    json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LiveComposer] 策略 JSON 解析失败: {ex.Message}");
            }
            if (sc != null)
            {
                var warmupDays = config.GetValue("Live:WarmupDays", 5);
                IBarStore? warmupStore = null;
                if (warmupDays > 0)
                {
                    var warmupDb = Path.Combine(dataPath, config["Live:WarmupDatabase"] ?? "bars_history.duckdb");
                    if (File.Exists(warmupDb)) warmupStore = new DuckDBStore(warmupDb, readOnly: true);
                }
                engineOptions = new EngineOptions
                {
                    StartTime = DateTime.Today, EndTime = DateTime.Today.AddDays(1),
                    Instruments = allInstruments, StrategyConfigs = [sc],
                    StartingCapital = sc.AllocatedCapital > 0 ? sc.AllocatedCapital : startCapital,
                    IsLive = true, WarmupDays = warmupDays, WarmupStore = warmupStore,
                };
                StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);
                // 策略品种强制加入 CtpLiveFeed 的 Activity Filter 活跃集
                foreach (var inst in sc.Instruments) _pendingStrategyInstruments.Add(inst);
                Console.Error.WriteLine($"[LiveComposer] Strategy instruments: {string.Join(",", sc.Instruments)}");
            }
        }
        services.AddSingleton(engineOptions);

        // ── 引擎 + 后台服务 ──
        services.AddSingleton(sp => new TradingEngine(
            sp.GetRequiredService<IDataFeed>(), execution, portfolio,
            sp.GetRequiredService<IndicatorManager>(), sp.GetRequiredService<StrategyContainer>(),
            risk, sp.GetRequiredService<FeedbackMonitor>(), sp.GetRequiredService<TickSnapshot>(),
            engineOptions, registry,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TradingStudio.Engine.TradingEngine>>()));

        services.AddHostedService<EngineHost>();
        services.AddHostedService<EngineHubPushService>();
        services.AddHostedService<LiveDataCollector>();
        services.AddHostedService<PeriodMaintainer>();
    }
}
