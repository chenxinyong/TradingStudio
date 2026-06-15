using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingStudio.Core.Models;
using TradingStudio.Data.Import;

namespace TradingStudio.ToolBox.ImportUrlTool;

/// <summary>
/// URL 远程导入 — 下载单个交易日 RAR → CSV → Bar → SQLite
/// 用法: ToolBox import-url --url <url> [--db bars.db] [--date yyyyMMdd]
/// </summary>
public class ImportUrlTool : IToolCommand
{
    public string Name => "import-url";
    public string? Alias => "iu";
    public string Description => "下载远程 RAR → CSV → Bar → SQLite";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddImportPipeline(config);
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var url = "";
        var dbPath = "bars.db";
        var dateStr = "";
        var symbolsFilter = new List<string>();

        for (int j = 0; j < args.Length; j++)
        {
            switch (args[j])
            {
                case "--url" or "-u" when j + 1 < args.Length:
                    url = args[++j]; break;
                case "--db" or "-d" when j + 1 < args.Length:
                    dbPath = args[++j]; break;
                case "--date" when j + 1 < args.Length:
                    dateStr = args[++j]; break;
                case "--symbol" or "-s" when j + 1 < args.Length:
                    symbolsFilter.Add(args[++j].ToLowerInvariant()); break;
            }
        }

        if (string.IsNullOrEmpty(url))
        {
            Console.Error.WriteLine("Usage: ToolBox import-url --url <url> [--db bars.db] [--date yyyyMMdd] [--symbol rb]");
            return 1;
        }

        var log = sp.GetRequiredService<ILogger<ImportUrlTool>>();
        log.LogInformation("Downloading: {Url}", url);

        // 下载到临时目录（按金数源目录结构：DataDir/FutAC_TickKZ_CTP_Daily_{year}/file.rar）
        var tempDir = Path.Combine(Path.GetTempPath(), $"jinshuyuan_{DateTime.Now:yyyyMMddHHmmss}");
        var year = dateStr.Length >= 4 ? dateStr[..4] : DateTime.Now.Year.ToString();
        var rarDir = Path.Combine(tempDir, $"FutAC_TickKZ_CTP_Daily_{year}");
        Directory.CreateDirectory(rarDir);
        var monthStr = dateStr.Length >= 6 ? dateStr[..6] : dateStr;  // YYYYMM format
        var rarFile = Path.Combine(rarDir, $"FutAC_TickKZ_CTP_Daily_{monthStr}.rar");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            await using var stream = await http.GetStreamAsync(url, ct);
            await using var file = File.Create(rarFile);
            await stream.CopyToAsync(file, ct);
            log.LogInformation("Downloaded: {Size} bytes → {Path}", new FileInfo(rarFile).Length, rarFile);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Download failed: {Url}", url);
            try { Directory.Delete(tempDir, true); } catch { }
            return 1;
        }

        // 加载品种注册表
        var symbolsPath = FindSymbolsJson();
        var registry = FutureRegistry.Load(symbolsPath);
        var knownProducts = new HashSet<string>(
            registry.All.Values.Select(p => p.Code.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var fromMonth = dateStr.Length >= 6 ? dateStr[..6] : "202001";
        var toMonth = fromMonth;

        var opts = new JinshuyuanOptions
        {
            DataDir = tempDir,
            DbPath = dbPath,
            Layer = "all",
            Symbols = new HashSet<string>(symbolsFilter, StringComparer.OrdinalIgnoreCase),
            FromMonth = fromMonth,
            ToMonth = toMonth,
            TempDir = tempDir,
            KnownProducts = knownProducts,
        };

        log.LogInformation("Importing: {Symbols} from {Url}",
            string.Join(",", symbolsFilter.Count > 0 ? symbolsFilter : ["all"]), url);

        try
        {
            await new JinshuyuanImportService(opts).ImportAsync(ct);
            log.LogInformation("Import complete → {Db}", dbPath);
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
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
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
        throw new FileNotFoundException("symbols.json not found");
    }
}
