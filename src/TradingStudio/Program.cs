using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Engine;
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
    Console.WriteLine("  TradingStudio backtest --config <strategy.json> [--db <path>]");
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

    // Windows Service
    builder.Host.UseWindowsService(o => o.ServiceName = "TradingStudio");

    // SignalR
    builder.Services.AddSignalR();
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

    // Serilog
    builder.Services.AddSerilog((_, cfg) =>
        cfg.ReadFrom.Configuration(builder.Configuration));

    // ── 引擎组件 DI ──
    var cfg = builder.Configuration;

    // 品种注册表
    var symbolsPath = cfg["Live:SymbolsPath"] ?? "symbols.json";
    var registry = FutureRegistry.Load(symbolsPath);
    builder.Services.AddSingleton(registry);

    // 数据源: CTP 行情
    var mdOpts = new CtpMdOptions
    {
        MdFront = cfg["Live:MdFront"] ?? "",
        BrokerId = cfg["Live:BrokerId"] ?? "9999",
        UserId = cfg["Live:UserId"] ?? "",
        Password = cfg["Live:Password"] ?? "",
    };
    var liveFeed = new CtpLiveFeed(mdOpts);
    builder.Services.AddSingleton<IDataFeed>(liveFeed);

    // 风控 + 执行
    var risk = new RiskController();
    builder.Services.AddSingleton(risk);
    var execution = new ExecutionHandler(risk);
    builder.Services.AddSingleton<IExecutionHandler>(execution);

    // 反馈
    var feedback = new FeedbackMonitor();
    builder.Services.AddSingleton(feedback);

    // 指标 + 策略容器
    var indicators = new IndicatorManager();
    builder.Services.AddSingleton(indicators);
    var strategies = new StrategyContainer();
    builder.Services.AddSingleton(strategies);

    // 资金管理
    var startCapital = decimal.Parse(cfg["Live:StartingCapital"] ?? "100000");
    var portfolio = new PortfolioManager(startCapital);
    builder.Services.AddSingleton(portfolio);

    // CTP 交易桥接
    if (!string.IsNullOrEmpty(cfg["Live:TraderFront"]))
    {
        var traderOpts = new CtpTraderOptions
        {
            TraderFront = cfg["Live:TraderFront"],
            BrokerId = cfg["Live:BrokerId"] ?? "9999",
            UserId = cfg["Live:UserId"] ?? "",
            Password = cfg["Live:Password"] ?? "",
        };
        var bridge = new CtpTraderBridge(execution.FillChannel, traderOpts);
        bridge.Connect();
        execution.SendToExchange = bridge.SendOrder;
    }
    execution.IsLive = true;

    // 引擎
    var engineOptions = new EngineOptions
    {
        StartTime = DateTime.Today,
        EndTime = DateTime.Today.AddDays(1),
        Instruments = registry.All.Values.Select(f => f.Code + "??")
            .Take(10).ToList(), // 默认前10个品种, 实际由策略配置覆盖
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
            engineOptions = new EngineOptions
            {
                StartTime = DateTime.Today,
                EndTime = DateTime.Today.AddDays(1),
                Instruments = strategyConfig.Instruments,
                StrategyConfigs = [strategyConfig],
                StartingCapital = strategyConfig.AllocatedCapital > 0 ? strategyConfig.AllocatedCapital : startCapital,
                IsLive = true,
            };
            StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);
        }
    }

    var engine = new TradingEngine(
        liveFeed, execution, portfolio, indicators, strategies,
        risk, feedback, engineOptions, registry);
    builder.Services.AddSingleton(engine);

    // 引擎后台运行
    builder.Services.AddHostedService<EngineHost>();

    var app = builder.Build();
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

    var cliArgs = new List<string>();
    if (overrides.TryGetValue("--db",    out var db))    cliArgs.Add($"Collect:Database={db}");
    if (overrides.TryGetValue("--tick",  out var tick))  cliArgs.Add($"Collect:TickData={tick}");
    if (overrides.TryGetValue("--front", out var front)) cliArgs.Add($"Collect:MdFront={front}");
    if (overrides.TryGetValue("--user",  out var user))  cliArgs.Add($"Collect:UserId={user}");
    if (overrides.TryGetValue("--pwd",   out var pwd))   cliArgs.Add($"Collect:Password={pwd}");
    if (overrides.TryGetValue("--broker", out var broker)) cliArgs.Add($"Collect:BrokerId={broker}");

    var builder = Host.CreateApplicationBuilder([..cliArgs, ..args]);
    builder.Services.AddSerilog((_, cfg) => cfg.ReadFrom.Configuration(builder.Configuration));
    var cfgSection = builder.Configuration.GetSection(CollectOptions.Section);
    builder.Services.Configure<CollectOptions>(cfgSection);
    builder.Services.PostConfigure<CollectOptions>(opts => { opts.ExchangeFilter = exchange; opts.SymbolFilter = symbol; });
    builder.Services.AddHostedService<CollectService>();
    await builder.Build().RunAsync();
}
