using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Storage;
using TradingStudio.Engine;
using TradingStudio.Data.Storage;
using TradingStudio.Live;
using TradingStudio.Options;
using TradingStudio.Services;

// ================================================================
// TradingStudio — 量化交易工作室
//
//   TradingStudio live                 启动实盘引擎 (Windows Service)
//   TradingStudio backtest [options]   回测引擎
//   TradingStudio collect [options]    行情采集
//
// 数据导入已迁移至: TradingStudio.ToolBox
// ================================================================

try
{
    if (args.Length == 0) { PrintUsage(); return; }

    switch (args[0])
    {
        case "live":     await RunLiveAsync(args[1..]);     break;
        case "backtest": await RunBacktestAsync(args[1..]); break;
        case "collect":  await RunCollectAsync(args[1..]);  break;
        default:         PrintUsage(); break;
    }
}
catch (Exception ex)
{
    // 保证任何致命错误在窗口消失前可见
    var msg = $@"
═══════════════════════════════════════
  FATAL ERROR — 进程即将退出
═══════════════════════════════════════
  {ex.GetType().Name}: {ex.Message}
═══════════════════════════════════════
";
    Console.Error.WriteLine(msg);
    try { File.AppendAllText("crash.log", msg + ex + "\n"); } catch { }
    Console.Error.WriteLine("按任意键退出...");
    try { Console.ReadKey(true); } catch { }
    Environment.Exit(1);
}

return;

static void PrintUsage()
{
    Console.WriteLine("TradingStudio — 量化交易工作室");
    Console.WriteLine();
    Console.WriteLine("用法:");
    Console.WriteLine("  TradingStudio live              启动实盘引擎 (Windows Service)");
    Console.WriteLine("  TradingStudio backtest --config <strategy.json> [--db <path>]  (默认: data/bars_history.duckdb)");
    Console.WriteLine("  TradingStudio collect [options]  行情采集");
    Console.WriteLine();
    Console.WriteLine("按任意键退出...");
    try { Console.ReadKey(true); } catch { }
}

static void PrintBanner()
{
    Console.WriteLine("════════════════════════════════");
    Console.WriteLine("  TradingStudio v0.2.0 行情采集");
    Console.WriteLine("════════════════════════════════");
    Console.WriteLine($"  .NET:    {Environment.Version}");
    Console.WriteLine($"  OS:      {Environment.OSVersion}");
    Console.WriteLine($"  x64:     {Environment.Is64BitProcess}");
    Console.WriteLine($"  CWD:     {Environment.CurrentDirectory}");
    Console.WriteLine($"  Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
    Console.WriteLine("════════════════════════════════");
}


// ═══════════════════════════════════════════════════════════════
// live — 实盘引擎 (Windows Service + REST API + SignalR Hub)
// ═══════════════════════════════════════════════════════════════
static async Task RunLiveAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    // 本地配置覆盖（含敏感凭证，不提交 Git）
    builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

    // Windows Service
    builder.Host.UseWindowsService(o => o.ServiceName = "TradingStudio");

    // SignalR
    builder.Services.AddSignalR();
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

    // Serilog
    builder.Services.AddSerilog((_, cfg) =>
        cfg.ReadFrom.Configuration(builder.Configuration));
    builder.Host.UseSerilog();  // 配置静态 Log.Logger（CtpTraderBridge 回调需要）

    // ── 启动配置验证 ──
    var cfg = builder.Configuration;
    ValidateLiveConfig(cfg);

    // ── 基础设施 (时段 + 健康) ──
    builder.Services.AddSingleton<SessionScheduler>();
    builder.Services.AddSingleton<HealthMonitor>();

    // ── 品种注册表 ──
    var symbolsPath = cfg["Live:SymbolsPath"] ?? "symbols.json";
    var registry = FutureRegistry.Load(symbolsPath);
    builder.Services.AddSingleton(registry);

    // ── 数据源: CTP 行情 ──
    var mdOpts = new CtpMdOptions
    {
        MdFront = cfg["Live:MdFront"]!,
        BrokerId = cfg["Live:BrokerId"] ?? "9999",
        UserId = cfg["Live:UserId"]!,
        Password = cfg["Live:Password"]!,
    };
    // 工厂注入：ILogger 在 app.Build() 后才可用
    var activityTracker = new ContractActivityTracker(observationSeconds: 60);
    builder.Services.AddSingleton(activityTracker);
    builder.Services.AddSingleton<IDataFeed>(sp =>
    {
        var feed = new CtpLiveFeed(mdOpts, sp.GetRequiredService<Serilog.ILogger>());
        feed.ActivityTracker = activityTracker;
        return feed;
    });
    builder.Services.AddSingleton(sp => (CtpLiveFeed)sp.GetRequiredService<IDataFeed>());

    // 风控阈值（从 appsettings.json Risk 段读取，缺失时使用安全默认值）
    var risk = new RiskController(
        maxPosition: int.Parse(cfg["Risk:MaxPositionPerInstrument"] ?? "5"),
        maxOrderQty: int.Parse(cfg["Risk:MaxOrderQuantity"] ?? "100"),
        maxDrawdown: decimal.Parse(cfg["Risk:MaxDrawdownPct"] ?? "0.25"));
    builder.Services.AddSingleton(risk);
    var execution = new ExecutionHandler(risk);
    builder.Services.AddSingleton<IExecutionHandler>(execution);
    builder.Services.AddSingleton(execution);  // EngineMonitorApi 直接依赖具体类型

    // 反馈 + 行情快照
    var feedback = new FeedbackMonitor();
    builder.Services.AddSingleton(feedback);
    var tickSnapshot = new TickSnapshot();
    builder.Services.AddSingleton(tickSnapshot);

    // 指标 + 策略容器
    var indicators = new IndicatorManager();
    builder.Services.AddSingleton(indicators);
    var strategies = new StrategyContainer();
    builder.Services.AddSingleton(strategies);

    // ── 数据持久化 ──
    var dataPath = cfg["Live:DataPath"] ?? "data";
    var dbPath = Path.Combine(dataPath, cfg["Live:Database"] ?? "bars_live.db");
    // 自动识别存储引擎：.duckdb 扩展名 → DuckDB, 否则 SQLite
    // UseDuckDB 可强制覆盖（兼容旧配置）
    var forceDuckDB = cfg["Live:UseDuckDB"]?.ToLowerInvariant() == "true";
    var isDuckDB = forceDuckDB || dbPath.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase);
    IBarStore barStore = isDuckDB
        ? new DuckDBStore(dbPath, enableTickPurge: true)
        : new SqliteBarStore(dbPath);
    builder.Services.AddSingleton(barStore);
    builder.Services.AddSingleton<TradingStudio.Data.Storage.BuildPeriodsService>();
    var tickWriter = new TickCsvWriter(Path.Combine(dataPath, "TickData"));
    builder.Services.AddSingleton(tickWriter);

    // 资金管理
    var startCapital = decimal.Parse(cfg["Live:StartingCapital"] ?? "100000");
    var portfolio = new PortfolioManager(startCapital);
    builder.Services.AddSingleton(portfolio);

    // CTP 交易桥接
    if (!string.IsNullOrEmpty(cfg["Live:TraderFront"]))
    {
        var traderOpts = new CtpTraderOptions
        {
            TraderFront = cfg["Live:TraderFront"]!,
            BrokerId = cfg["Live:BrokerId"] ?? "9999",
            UserId = cfg["Live:UserId"] ?? "",
            Password = cfg["Live:Password"] ?? "",
            AuthCode = cfg["Live:AuthCode"] ?? "0000000000000000",
            AppId = cfg["Live:AppId"] ?? "simnow_client_test",
        };
        // CtpTraderBridge: 先注册，app.Build() 后由 EngineHost 调用 Connect
        var bridge = new CtpTraderBridge(execution.FillChannel, traderOpts);
        builder.Services.AddSingleton(bridge);
        execution.SendToExchange = bridge.SendOrder;
    }
    execution.IsLive = true;

    // 引擎 — Live 模式全品种订阅（数据采集需全量 Tick）
    // ContractCodeGenerator 将品种代码展开为实际合约代码（如 "ag" → "ag2608","ag2609"...）
    var allInstruments = ContractCodeGenerator.BatchSubscribe(registry.All.Values, 50)
        .SelectMany(b => b)
        .ToList();
    Console.WriteLine($"Live: {allInstruments.Count} contracts from {registry.All.Count} products");
    var engineOptions = new EngineOptions
    {
        StartTime = DateTime.Today,
        EndTime = DateTime.Today.AddDays(1),
        Instruments = allInstruments,
        StartingCapital = startCapital,
        IsLive = true,
    };
    builder.Services.AddSingleton(engineOptions);

    // 加载策略
    var strategyConfigPath = cfg["Live:StrategyConfig"];
    if (!string.IsNullOrEmpty(strategyConfigPath) && File.Exists(strategyConfigPath))
    {
        var json = File.ReadAllText(strategyConfigPath);
        var strategyConfig = System.Text.Json.JsonSerializer.Deserialize<TradingStudio.Core.Strategy.StrategyConfig>(
            json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (strategyConfig != null)
        {
            var warmupDays = int.Parse(cfg["Live:WarmupDays"] ?? "5");
            IBarStore? warmupStore = null;
            if (warmupDays > 0)
            {
                var warmupDb = Path.Combine(dataPath, cfg["Live:WarmupDatabase"] ?? "bars_history.duckdb");
                if (File.Exists(warmupDb))
                    warmupStore = new DuckDBStore(warmupDb, readOnly: true);
            }
            engineOptions = new EngineOptions
            {
                StartTime = DateTime.Today,
                EndTime = DateTime.Today.AddDays(1),
                Instruments = allInstruments,
                StrategyConfigs = [strategyConfig],
                StartingCapital = strategyConfig.AllocatedCapital > 0 ? strategyConfig.AllocatedCapital : startCapital,
                IsLive = true,
                WarmupDays = warmupDays,
                WarmupStore = warmupStore,
            };
            StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);
        }
    }

    // 工厂创建引擎（IDataFeed 需延迟解析）
    builder.Services.AddSingleton(sp => new TradingEngine(
        sp.GetRequiredService<IDataFeed>(), execution, portfolio, indicators, strategies,
        risk, feedback, tickSnapshot, engineOptions, registry,
        sp.GetService<Microsoft.Extensions.Logging.ILogger<TradingStudio.Engine.TradingEngine>>()));

    // 引擎后台运行 + SignalR 实时推送 + 数据落盘
    builder.Services.AddHostedService<EngineHost>();
    builder.Services.AddHostedService<EngineHubPushService>();
    builder.Services.AddHostedService<LiveDataCollector>();
    builder.Services.AddHostedService<PeriodMaintainer>();  // 自动维护 5min/15min/week

    var app = builder.Build();

    // app.Build() 后静态 Logger 已配置，启动交易桥接
    var traderBridge = app.Services.GetService<CtpTraderBridge>();
    traderBridge?.Connect();

    app.UseCors();

    // REST API
    TradingStudio.EngineMonitorApi.MapEndpoints(app);

    // SignalR Hub
    app.MapHub<TradingStudio.EngineHub>("/hubs/engine");

    await app.RunAsync();
}


// ═══════════════════════════════════════════════════════════════
// backtest — 回测引擎
// ═══════════════════════════════════════════════════════════════
static async Task RunBacktestAsync(string[] args)
{
    var exitCode = await TradingStudio.Commands.BacktestCommand.RunAsync(args);
    Environment.Exit(exitCode);
}


static async Task RunCollectAsync(string[] args)
{
    PrintBanner();

    // 全局崩溃日志
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        var ex = e.ExceptionObject as Exception;
        var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL: {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n";
        try { File.AppendAllText("crash.log", msg); } catch { }
        Environment.Exit(1);
    };
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNOBSERVED: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n";
        try { File.AppendAllText("crash.log", msg); } catch { }
        e.SetObserved();
    };

    string? exchange = null, symbol = null;
    var i = 0;
    var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    while (i < args.Length)
    {
        if (args[i].StartsWith("--") && i + 1 < args.Length) { overrides[args[i]] = args[i + 1]; i += 2; }
        else if (!args[i].StartsWith("--")) { if (exchange is null) exchange = args[i]; else if (symbol is null) symbol = args[i]; i++; }
        else { i++; }
    }

    var collectorArgs = new List<string>();
    if (overrides.TryGetValue("--db",    out var db))    collectorArgs.Add($"Collect:Database={db}");
    if (overrides.TryGetValue("--tick",  out var tick))  collectorArgs.Add($"Collect:TickData={tick}");
    if (overrides.TryGetValue("--front", out var front)) collectorArgs.Add($"Collect:MdFront={front}");
    if (overrides.TryGetValue("--user",  out var user))  collectorArgs.Add($"Collect:UserId={user}");
    if (overrides.TryGetValue("--pwd",   out var pwd))   collectorArgs.Add($"Collect:Password={pwd}");
    if (overrides.TryGetValue("--broker", out var broker)) collectorArgs.Add($"Collect:BrokerId={broker}");
    var useDuckCollect = overrides.ContainsKey("--duckdb") || args.Contains("--duckdb");
    if (useDuckCollect) collectorArgs.Add("Collect:UseDuckDB=true");

    var builder = Host.CreateApplicationBuilder([..collectorArgs, ..args]);
    builder.Services.AddSerilog((_, cfg) => cfg.ReadFrom.Configuration(builder.Configuration));
    var cfgSection = builder.Configuration.GetSection(CollectOptions.Section);
    builder.Services.Configure<CollectOptions>(cfgSection);
    builder.Services.PostConfigure<CollectOptions>(opts => { opts.ExchangeFilter = exchange; opts.SymbolFilter = symbol; });

    // ── 基础设施 ──
    builder.Services.AddSingleton<SessionScheduler>();
    builder.Services.AddSingleton<HealthMonitor>();

    // ── 数据持久化（DI 单例，PeriodMaintainer 共享）──
    var collectCfg = builder.Configuration.GetSection(CollectOptions.Section).Get<CollectOptions>()!;
    var isDuck = collectCfg.UseDuckDB || collectCfg.Database.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase);
    IBarStore barStore = isDuck
        ? new DuckDBStore(collectCfg.Database, enableTickPurge: true)
        : new SqliteBarStore(collectCfg.Database);
    builder.Services.AddSingleton(barStore);
    builder.Services.AddSingleton(new TickCsvWriter(collectCfg.TickData));
    builder.Services.AddSingleton<TradingStudio.Data.Storage.BuildPeriodsService>();

    builder.Services.AddHostedService<CollectService>();
    builder.Services.AddHostedService<PeriodMaintainer>();
    await builder.Build().RunAsync();
}

static void ValidateLiveConfig(IConfiguration cfg)
{
    var errors = new List<string>();

    if (string.IsNullOrEmpty(cfg["Live:UserId"]))
        errors.Add("Live:UserId is required — check appsettings.json → Live.UserId");
    if (string.IsNullOrEmpty(cfg["Live:Password"]))
        errors.Add("Live:Password is required — check appsettings.json → Live.Password");
    if (string.IsNullOrEmpty(cfg["Live:MdFront"]))
        errors.Add("Live:MdFront is required — check appsettings.json → Live.MdFront");

    if (errors.Count > 0)
    {
        Console.Error.WriteLine("=== 配置错误 ===");
        foreach (var e in errors) Console.Error.WriteLine($"  - {e}");
        Console.Error.WriteLine("================");
        throw new InvalidOperationException(string.Join("\n", errors));
    }
}
