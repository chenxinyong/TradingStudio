using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingStudio.Core.Models;
using TradingStudio.Data.Import;

namespace TradingStudio.ToolBox.JinshuyuanTool;

/// <summary>
/// 金数源 RAR 历史数据导入工具 — RAR → CSV → Bar → SQLite
/// 用法: ToolBox import-jinshuyuan --data-dir <dir> --layer main [--symbol rb] ...
/// </summary>
public class JinshuyuanTool : IToolCommand
{
    public string Name => "import-jinshuyuan";
    public string? Alias => "ij";
    public string Description => "金数源 RAR → Bar → SQLite";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddImportPipeline(config);
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var layer = "";
        var symbols = new List<string>();
        string? exchangeStr = null;
        var fromMonth = "202001";
        var toMonth = "202512";
        var dataDir = @"C:\Works\Datas\Jinshuyuan";
        var dbPath = "bars_history.db";
        var dryRun = false;

        for (int j = 0; j < args.Length; j++)
        {
            switch (args[j])
            {
                case "--layer" or "-l" when j + 1 < args.Length:    layer = args[++j]; break;
                case "--symbol" or "-s" when j + 1 < args.Length:   symbols.Add(args[++j].ToLowerInvariant()); break;
                case "--exchange" or "-e" when j + 1 < args.Length: exchangeStr = args[++j].ToUpperInvariant(); break;
                case "--from" or "-f" or "--from-month" when j + 1 < args.Length: fromMonth = args[++j]; break;
                case "--to" or "-t" or "--to-month" when j + 1 < args.Length:     toMonth = args[++j]; break;
                case "--data-dir" or "-d" when j + 1 < args.Length: dataDir = args[++j]; break;
                case "--db" when j + 1 < args.Length:               dbPath = args[++j]; break;
                case "--dry-run": dryRun = true; break;
            }
        }

        if (layer is not ("main" or "active" or "all"))
        {
            Console.Error.WriteLine("Error: --layer is required and must be 'main', 'active', or 'all'");
            return 1;
        }

        var log = sp.GetRequiredService<ILogger<JinshuyuanTool>>();

        // 加载品种注册表
        var symbolsPath = FindSymbolsJson();
        var registry = FutureRegistry.Load(symbolsPath);
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

        log.LogInformation("Jinshuyuan import: {Symbols} {Layer} {From}→{To} ({Dir})",
            string.Join(",", symbols.Count > 0 ? symbols : ["all"]),
            layer, fromMonth, toMonth, dataDir);

        try
        {
            await new JinshuyuanImportService(opts).ImportAsync(ct);
            log.LogInformation("Import complete");
            return 0;
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("Import cancelled");
            return 1;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Import failed");
            return 1;
        }
    }

    private static string FindSymbolsJson()
    {
        var exeDir = AppContext.BaseDirectory;
        var path = Path.Combine(exeDir, "symbols.json");
        if (File.Exists(path)) return path;
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "symbols.json");
        if (File.Exists(cwd)) return cwd;
        var src = Path.Combine(Directory.GetCurrentDirectory(), "..", "TradingStudio", "symbols.json");
        if (File.Exists(src)) return Path.GetFullPath(src);
        throw new FileNotFoundException("symbols.json not found in exe dir, current dir, or ../TradingStudio/");
    }
}
