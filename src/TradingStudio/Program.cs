using TradingStudio.Core.Models;
using TradingStudio.Data.Import;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TradingStudio.Options;
using TradingStudio.Services;

// ================================================================
// TradingStudio — 量化交易工作室
//
// 模式:
//   TradingStudio                       启动实盘引擎 (Windows Service / 控制台)
//   TradingStudio collect [args]        行情采集 (Phase 1)
//   TradingStudio import [args]         金数源 CSV 导入
//   TradingStudio import-jinshuyuan     金数源 RAR 导入
//   TradingStudio backtest [args]       回测 (Phase 2)
// ================================================================

var mode = args.Length > 0 ? args[0] : "live";

if (mode is "live")
{
    await RunLiveAsync(args);
    return;
}

if (mode is "backtest")
{
    await RunBacktestAsync(args[1..]);
    return;
}

// ── 以下为 Phase 1 命令 (不变) ────────────────────────

if (mode is "import-jinshuyuan") { await RunImportJinshuyuanAsync(args); return; }
if (mode is "import") { await RunImportAsync(args); return; }

// 默认: collect 模式
await RunCollectAsync(args);
return;


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

    // 引擎组件 (Phase 3 注入)
    // builder.Services.AddSingleton<IDataFeed, CtpLiveFeed>();
    // builder.Services.AddSingleton<PortfolioManager>();
    // builder.Services.AddSingleton<StrategyContainer>();
    // builder.Services.AddSingleton<IndicatorManager>();
    // builder.Services.AddSingleton<ExecutionHandler>();
    // builder.Services.AddSingleton<FeedbackMonitor>();
    // builder.Services.AddSingleton<TradingEngine>();

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
    Console.WriteLine("TradingStudio Backtest — Phase 2a (待实现)");
    Console.WriteLine("Usage: TradingStudio backtest --config <strategy.json> [--mode bar|tick]");
    await Task.CompletedTask;
}


// ═══════════════════════════════════════════════════════════════
// Phase 1 命令 (从原 Program.cs 迁移, 逻辑不变)
// ═══════════════════════════════════════════════════════════════

static async Task RunImportJinshuyuanAsync(string[] args)
{
    var layer = "";
    var symbols = new List<string>();
    string? exchangeStr = null;
    var fromMonth = "202101"; var toMonth = "202212";
    var dataDir = @"C:\Works\Datas\Jinshuyuan";
    var dbPath = "bars_history.db";
    var dryRun = false;

    for (int j = 1; j < args.Length; j++)
    {
        switch (args[j])
        {
            case "--layer" when j + 1 < args.Length:    layer = args[++j]; break;
            case "--symbol" when j + 1 < args.Length:   symbols.Add(args[++j].ToLowerInvariant()); break;
            case "--exchange" when j + 1 < args.Length: exchangeStr = args[++j].ToUpperInvariant(); break;
            case "--from" or "--from-month" when j + 1 < args.Length: fromMonth = args[++j]; break;
            case "--to" or "--to-month" when j + 1 < args.Length:     toMonth = args[++j]; break;
            case "--data-dir" when j + 1 < args.Length: dataDir = args[++j]; break;
            case "--db" when j + 1 < args.Length:       dbPath = args[++j]; break;
            case "--dry-run": dryRun = true; break;
        }
    }

    if (layer is not ("main" or "active" or "all"))
    {
        Console.WriteLine("Error: --layer is required and must be 'main', 'active', or 'all'");
        Environment.Exit(1);
    }

    var symbolsJson = FindSymbolsJson();
    var registry = FutureRegistry.Load(symbolsJson);
    var knownProducts = new HashSet<string>(
        registry.All.Values.Select(p => p.Code.ToLowerInvariant()),
        StringComparer.OrdinalIgnoreCase);

    var opts = new JinshuyuanOptions
    {
        DataDir = dataDir, DbPath = dbPath, Layer = layer,
        Symbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase),
        ExchangeCode = exchangeStr, FromMonth = fromMonth, ToMonth = toMonth,
        DryRun = dryRun, KnownProducts = knownProducts,
    };

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try
    {
        await new JinshuyuanImportService(opts).ImportAsync(cts.Token);
        Environment.Exit(0);
    }
    catch (OperationCanceledException) { Console.WriteLine("\nImport cancelled."); Environment.Exit(1); }
    catch (Exception ex) { Console.WriteLine($"Import failed: {ex.Message}"); Environment.Exit(1); }
}

static async Task RunImportAsync(string[] args)
{
    var input = ""; var dbPath = "bars.db";
    for (int j = 1; j < args.Length; j++)
    {
        if (args[j] == "--input" && j + 1 < args.Length) { input = args[++j]; }
        else if (args[j] == "--db" && j + 1 < args.Length) { dbPath = args[++j]; }
    }
    if (string.IsNullOrEmpty(input)) { Console.WriteLine("Usage: TradingStudio import --input <file|dir> [--db bars.db]"); Environment.Exit(1); }

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try
    {
        if (Directory.Exists(input)) await TickImportService.ImportDirectoryAsync(input, dbPath, ct: cts.Token);
        else if (File.Exists(input)) await TickImportService.ImportFileAsync(input, dbPath, cts.Token);
        else { Console.WriteLine($"Error: path not found — {input}"); Environment.Exit(1); }
        Console.WriteLine("Import complete."); Environment.Exit(0);
    }
    catch (OperationCanceledException) { Console.WriteLine("\nImport cancelled."); Environment.Exit(1); }
    catch (Exception ex) { Console.WriteLine($"Import failed: {ex.Message}"); Environment.Exit(1); }
}

static async Task RunCollectAsync(string[] args)
{
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

static string FindSymbolsJson()
{
    var exeDir = AppContext.BaseDirectory;
    var path = Path.Combine(exeDir, "symbols.json");
    if (File.Exists(path)) return path;
    var cwd = Path.Combine(Directory.GetCurrentDirectory(), "symbols.json");
    if (File.Exists(cwd)) return cwd;
    throw new FileNotFoundException("symbols.json not found in exe dir or current dir");
}
