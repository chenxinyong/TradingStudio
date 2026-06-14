using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingStudio.Data.Import;

namespace TradingStudio.ToolBox.ImportTool;

/// <summary>
/// CSV Tick 导入工具 — 将金数源格式 CSV 聚合为 Bar 写入 SQLite
/// 用法: ToolBox import -i <csv-file|dir> [-d bars.db]
/// </summary>
public class ImportTool : IToolCommand
{
    public string Name => "import";
    public string? Alias => "i";
    public string Description => "CSV Tick → Bar → SQLite";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddImportPipeline(config);
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var input = "";
        var dbPath = "bars.db";

        for (int j = 0; j < args.Length; j++)
        {
            if (args[j] is "--input" or "-i" && j + 1 < args.Length) input = args[++j];
            else if (args[j] is "--db" or "-d" && j + 1 < args.Length) dbPath = args[++j];
        }

        if (string.IsNullOrEmpty(input))
        {
            Console.Error.WriteLine("Usage: ToolBox import --input <file|dir> [--db bars.db]");
            return 1;
        }

        var log = sp.GetRequiredService<ILogger<ImportTool>>();
        log.LogInformation("Import started: {Input} → {Db}", input, dbPath);

        try
        {
            if (Directory.Exists(input))
                await TickImportService.ImportDirectoryAsync(input, dbPath, ct: ct);
            else if (File.Exists(input))
                await TickImportService.ImportFileAsync(input, dbPath, ct);
            else
            {
                log.LogError("Path not found: {Path}", input);
                return 1;
            }

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
}
