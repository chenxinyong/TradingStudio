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
// === collect — 行情采集 ===
//   TradingStudio [exchange] [symbol] [options]
//
//   位置参数:
//     exchange   交易所过滤  SHFE|DCE|CZCE|CFFEX|INE|GFEX
//     symbol     品种或合约  ag(品种) | ag2608(合约)
//
//   可选参数:
//     --db PATH      数据库路径      默认 bars.db
//     --tick PATH    Tick输出目录    默认 TickData
//     --front ADDR   CTP行情前端     默认 appsettings.json
//     --broker ID    期货公司代码     默认 appsettings.json
//     --user ID      用户代码         默认 appsettings.json
//     --pwd PASS     密码             默认 appsettings.json
//
//   示例:
//     TradingStudio                                       # 全市场
//     TradingStudio SHFE ag                               # 上期所白银
//     TradingStudio SHFE ag2608 --db silver.db            # 指定库
//     TradingStudio DCE i --tick IronTicks --db iron.db   # 铁矿石
//
// === import — 金数源 CSV 历史数据导入 ===
//   TradingStudio import --input <file|dir> [--db bars.db]
//
//   示例:
//     TradingStudio import --input data\ticks\cu1603.csv
//     TradingStudio import --input data\ticks\             # 批量导入目录下所有 CSV
//     TradingStudio import --input data\ticks\ --db cu.db
//
// === import-jinshuyuan — 金数源 RAR 历史数据导入 ===
//   TradingStudio import-jinshuyuan --layer <main|active|all> [options]
//
//   示例:
//     TradingStudio import-jinshuyuan --layer main --dry-run
//     TradingStudio import-jinshuyuan --layer main --db bars_history.db
//     TradingStudio import-jinshuyuan --layer active --symbol ag --symbol cu
// ================================================================

// ── import-jinshuyuan 子命令 — 金数源 RAR 历史数据导入 ─────
if (args.Length > 0 && args[0] == "import-jinshuyuan")
{
    var layer = "";
    var symbols = new List<string>();
    string? exchangeStr = null;
    var fromMonth = "202101";
    var toMonth = "202212";
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
            case "--from" when j + 1 < args.Length:     fromMonth = args[++j]; break;
            case "--to" when j + 1 < args.Length:       toMonth = args[++j]; break;
            case "--data-dir" when j + 1 < args.Length: dataDir = args[++j]; break;
            case "--db" when j + 1 < args.Length:       dbPath = args[++j]; break;
            case "--dry-run": dryRun = true; break;
        }
    }

    if (layer is not ("main" or "active" or "all"))
    {
        Console.WriteLine("Error: --layer is required and must be 'main', 'active', or 'all'");
        Console.WriteLine("Usage: TradingStudio import-jinshuyuan --layer <main|active|all> [options]");
        Environment.Exit(1);
    }

    // 加载 symbols.json → 提取所有品种代码 (--layer active 用)
    var symbolsJson = FindSymbolsJson();
    var registry = FutureRegistry.Load(symbolsJson);
    var knownProducts = new HashSet<string>(
        registry.All.Values.Select(p => p.Code.ToLowerInvariant()),
        StringComparer.OrdinalIgnoreCase);

    var opts = new JinshuyuanOptions
    {
        DataDir = dataDir,
        DbPath = dbPath,
        Layer = layer,
        Symbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase),
        ExchangeCode = exchangeStr,
        FromMonth = fromMonth,
        ToMonth = toMonth,
        DryRun = dryRun,
        KnownProducts = knownProducts,
    };

    var service = new JinshuyuanImportService(opts);
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        await service.ImportAsync(cts.Token);
        Environment.Exit(0);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\nImport cancelled.");
        Environment.Exit(1);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Import failed: {ex.Message}");
        Environment.Exit(1);
    }
}

/// <summary>查找 symbols.json: 先找 exe 目录，再找当前目录</summary>
static string FindSymbolsJson()
{
    var exeDir = AppContext.BaseDirectory;
    var path = Path.Combine(exeDir, "symbols.json");
    if (File.Exists(path)) return path;
    var cwd = Path.Combine(Directory.GetCurrentDirectory(), "symbols.json");
    if (File.Exists(cwd)) return cwd;
    throw new FileNotFoundException("symbols.json not found in exe dir or current dir");
}

// ── import 子命令 ──────────────────────────────────────────
if (args.Length > 0 && args[0] == "import")
{
    var input = "";
    var dbPath = "bars.db";
    for (int j = 1; j < args.Length; j++)
    {
        if (args[j] == "--input" && j + 1 < args.Length) { input = args[++j]; }
        else if (args[j] == "--db" && j + 1 < args.Length) { dbPath = args[++j]; }
    }

    if (string.IsNullOrEmpty(input))
    {
        Console.WriteLine("Usage: TradingStudio import --input <file|dir> [--db bars.db]");
        Environment.Exit(1);
    }

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        if (Directory.Exists(input))
            await TickImportService.ImportDirectoryAsync(input, dbPath, ct: cts.Token);
        else if (File.Exists(input))
            await TickImportService.ImportFileAsync(input, dbPath, cts.Token);
        else
        {
            Console.WriteLine($"Error: path not found — {input}");
            Environment.Exit(1);
        }
        Console.WriteLine("Import complete.");
        Environment.Exit(0);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\nImport cancelled.");
        Environment.Exit(1);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Import failed: {ex.Message}");
        Environment.Exit(1);
    }
}

// 全局崩溃日志 — 在 Serilog 初始化之前兜底
// CTP 原生回调中的未处理异常可能绕过 Serilog，写 crash.log 确保有痕迹
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = e.ExceptionObject as Exception;
    var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL: {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n";
    try { File.AppendAllText("crash.log", msg); } catch { }
    Environment.Exit(1); // 确保进程退出，不留僵尸
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNOBSERVED: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n";
    try { File.AppendAllText("crash.log", msg); } catch { }
    e.SetObserved();
};

// 1. 位置参数
string? exchange = null, symbol = null;
var i = 0;

// 2. 可选参数
var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
while (i < args.Length)
{
    if (args[i].StartsWith("--") && i + 1 < args.Length)
    {
        overrides[args[i]] = args[i + 1];
        i += 2;
    }
    else if (!args[i].StartsWith("--"))
    {
        if (exchange is null) exchange = args[i];
        else if (symbol is null) symbol = args[i];
        i++;
    }
    else { i++; }
}

// 注入命令行覆盖到 IConfiguration（优先级高于 appsettings.json）
var cliArgs = new List<string>();
if (overrides.TryGetValue("--db",    out var db))    cliArgs.Add($"Collect:Database={db}");
if (overrides.TryGetValue("--tick",  out var tick))  cliArgs.Add($"Collect:TickData={tick}");
if (overrides.TryGetValue("--front", out var front)) cliArgs.Add($"Collect:MdFront={front}");
if (overrides.TryGetValue("--user",  out var user))  cliArgs.Add($"Collect:UserId={user}");
if (overrides.TryGetValue("--pwd",   out var pwd))   cliArgs.Add($"Collect:Password={pwd}");
if (overrides.TryGetValue("--broker", out var broker)) cliArgs.Add($"Collect:BrokerId={broker}");

var builder = Host.CreateApplicationBuilder([..cliArgs, ..args]);

builder.Services.AddSerilog((_, cfg) =>
    cfg.ReadFrom.Configuration(builder.Configuration));

var cfgSection = builder.Configuration.GetSection(CollectOptions.Section);
builder.Services.Configure<CollectOptions>(cfgSection);
builder.Services.PostConfigure<CollectOptions>(opts =>
{
    opts.ExchangeFilter = exchange;
    opts.SymbolFilter   = symbol;
});

builder.Services.AddHostedService<CollectService>();

await builder.Build().RunAsync();
