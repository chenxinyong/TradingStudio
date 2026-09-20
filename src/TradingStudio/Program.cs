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
using TradingStudio.Commands;
using TradingStudio.Services;

// ================================================================
// TradingStudio — 量化交易工作室
//
//   TradingStudio live                 启动实盘引擎 (Windows Service)
//   TradingStudio backtest [options]   回测引擎
//   TradingStudio walkforward [opts]   Walk-Forward 参数优化
//   TradingStudio collect [options]    行情采集
//   TradingStudio factor-eval [opts]   因子IC评估 (Parquet/DuckDB)
//   TradingStudio carry-eval [opts]    Carry反转因子复现评估
//
// 数据导入已迁移至: TradingStudio.ToolBox
// ================================================================

try
{
    if (args.Length == 0) { PrintUsage(); return; }

    // 1. 获取当前运行环境
    string environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
    Console.WriteLine($"当前运行环境：{environment}");

    // 2. 分层加载配置
    IConfiguration config = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        // 基础配置（必选）
        .AddJsonFile("appsettings.json", false, true)
        // 环境差异化配置（可选）
        .AddJsonFile($"appsettings.{environment}.json", true, true)
        // 本地敏感配置（凭据，不提交 Git）
        .AddJsonFile("appsettings.local.json", true, true)
        .Build();

    switch (args[0])
    {
        case "live":        await RunLiveAsync(args[1..], config);        break;
        case "backtest":    await RunBacktestAsync(args[1..], config);    break;
        case "walkforward": await RunWalkForwardAsync(args[1..], config); break;
        case "collect":     await RunCollectAsync(args[1..], config);     break;
        case "factor-eval":  await FactorEvalCommand.RunAsync(args[1..]);  break;
        case "carry-eval":   await CarryEvalCommand.RunAsync(args[1..]);   break;
        case "param-scan":  await ParamScanCommand.RunAsync(args[1..]);  break;
        default:            PrintUsage(); break;
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
    Console.WriteLine("  TradingStudio v0.4.0 行情采集");
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
static async Task RunLiveAsync(string[] args, IConfiguration config)
{
    ConsoleGuard.DisableQuickEdit();   // QuickEdit 划选会冻结同步 Console 日志 → 曾致采集阻塞

    var builder = WebApplication.CreateBuilder(args);

    // 非交易时段也能启动HTTP
    builder.WebHost.ConfigureKestrel(o => o.ConfigureEndpointDefaults(ep => { }));
    var httpUrl = config["Urls"]?.Split(';').FirstOrDefault(u => u.StartsWith("http:")) ?? "http://0.0.0.0:59661";
    builder.WebHost.UseUrls(httpUrl);
    builder.Host.UseWindowsService(o => o.ServiceName = "TradingStudio");

    builder.Services.AddSignalR();
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
    builder.Services.AddSerilog((_, cfg) => cfg.ReadFrom.Configuration(config));
    builder.Host.UseSerilog();

    ValidateLiveConfig(config);
    LiveComposer.Configure(builder, config);

    var app = builder.Build();

    app.UseCors();
    TradingStudio.EngineMonitorApi.MapEndpoints(app);
    app.MapHub<TradingStudio.EngineHub>("/hubs/engine");

    Log.Information("TradingStudio HTTP server starting on {Urls}...", string.Join(", ", app.Urls));
    await app.RunAsync();
}


// ═══════════════════════════════════════════════════════════════
// backtest — 回测引擎
// ═══════════════════════════════════════════════════════════════
static async Task RunBacktestAsync(string[] args, IConfiguration config)
{
    var exitCode = await TradingStudio.Commands.BacktestCommand.RunAsync(args);
    Environment.Exit(exitCode);
}

static async Task RunWalkForwardAsync(string[] args, IConfiguration config)
{
    var exitCode = await TradingStudio.Commands.WalkForwardCommand.RunAsync(args);
    Environment.Exit(exitCode);
}

static async Task RunCollectAsync(string[] args, IConfiguration config)
{
    ConsoleGuard.DisableQuickEdit();   // QuickEdit 划选会冻结同步 Console 日志 → 曾致采集阻塞 3h38m
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
