using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingStudio.ToolBox.VerifyTool;

/// <summary>
/// 历史数据验证工具 — 六维度质量检查
/// 用法: ToolBox verify --db bars_2020.db [--output report.json] [--sample 5]
/// </summary>
public class VerifyTool : IToolCommand
{
    public string Name => "verify";
    public string? Alias => "v";
    public string Description => "六维度 Bar 数据质量验证";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<VerifyService>();
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var dbPath = "";
        var outputPath = "";
        var sampleCount = 5;
        var verbose = false;

        for (int j = 0; j < args.Length; j++)
        {
            if (args[j] is "--db" or "-d" && j + 1 < args.Length) dbPath = args[++j];
            else if (args[j] is "--output" or "-o" && j + 1 < args.Length) outputPath = args[++j];
            else if (args[j] is "--sample" or "-s" && j + 1 < args.Length) sampleCount = int.Parse(args[++j]);
            else if (args[j] is "--verbose" or "-v") verbose = true;
        }

        if (string.IsNullOrEmpty(dbPath))
        {
            Console.Error.WriteLine("Usage: ToolBox verify --db <bars.db> [--output report.json] [--sample 5] [--verbose]");
            return 1;
        }

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Database not found: {dbPath}");
            return 1;
        }

        var log = sp.GetRequiredService<ILogger<VerifyTool>>();
        var service = sp.GetRequiredService<VerifyService>();

        log.LogInformation("Verify started: {Db}", dbPath);

        try
        {
            var report = await service.RunAsync(dbPath, sampleCount, verbose, ct);

            // Console summary
            PrintSummary(report, dbPath, verbose);

            // JSON output
            if (!string.IsNullOrEmpty(outputPath))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(report,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                await File.WriteAllTextAsync(outputPath, json, ct);
                Console.WriteLine($"  Report saved: {outputPath}");
            }

            // Exit code: 0 = clean, 1 = issues found, 2 = severe
            return report.Rating switch
            {
                VerifyRating.Clean => 0,
                VerifyRating.Warning => 1,
                VerifyRating.Severe => 2,
                _ => 0
            };
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("Verify cancelled");
            return 1;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Verify failed");
            Console.Error.WriteLine($"Verify failed: {ex.Message}");
            return 1;
        }
    }

    private static void PrintSummary(VerifyReport r, string dbPath, bool verbose)
    {
        var sizeStr = r.FileSizeBytes switch
        {
            < 1024 => $"{r.FileSizeBytes} B",
            < 1024 * 1024 => $"{r.FileSizeBytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{r.FileSizeBytes / (1024.0 * 1024):F1} MB",
            _ => $"{r.FileSizeBytes / (1024.0 * 1024 * 1024):F2} GB"
        };

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════");
        Console.WriteLine("  Data Verification Report");
        Console.WriteLine("═══════════════════════════════════════");
        Console.WriteLine($"  DB:      {Path.GetFileName(dbPath)}  ({sizeStr})  [{r.Engine}]");
        Console.WriteLine($"  Range:   {r.DateMin:yyyy-MM-dd} ~ {r.DateMax:yyyy-MM-dd}");
        Console.WriteLine($"  Tables:  bars_1min={r.Bars1Min.RowCount:N0}  bars_day={r.BarsDay.RowCount:N0}");
        Console.WriteLine($"  Instruments: {r.Bars1Min.InstrumentCount} / {r.BarsDay.InstrumentCount}");
        Console.WriteLine("═══════════════════════════════════════");

        PrintDimension("Completeness", r.Completeness);
        PrintDimension("Consistency",  r.Consistency);
        PrintDimension("Continuity",   r.Continuity);
        PrintDimension("Accuracy",     r.Accuracy);
        PrintDimension("TradingDay",   r.TradingDay);
        PrintDimension("Dedup",        r.Dedup);

        Console.WriteLine("═══════════════════════════════════════");
        var ratingColor = r.Rating switch
        {
            VerifyRating.Clean => "PASS - Ready for backtest",
            VerifyRating.Warning => "WARN - Review items above before backtest",
            VerifyRating.Severe => "FAIL - Data quality issues must be fixed",
            _ => "?"
        };
        Console.WriteLine($"  Rating: {ratingColor}");
        Console.WriteLine("═══════════════════════════════════════");
        Console.WriteLine();
    }

    private static void PrintDimension(string name, DimensionResult d)
    {
        var icon = d.Status switch
        {
            DimensionStatus.Pass => "PASS",
            DimensionStatus.Warn => "WARN",
            DimensionStatus.Fail => "FAIL",
            _ => "SKIP"
        };

        Console.WriteLine($"  [{icon}] {name,-14} {d.Summary}");
        foreach (var detail in d.Details.Take(5))
            Console.WriteLine($"         {detail}");
        if (d.Details.Count > 5)
            Console.WriteLine($"         ... and {d.Details.Count - 5} more");
    }
}
