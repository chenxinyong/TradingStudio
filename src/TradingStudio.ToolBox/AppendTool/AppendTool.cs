using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingStudio.ToolBox.AppendTool;

/// <summary>
/// 每日数据追加到历史库 — 自动去重
/// 用法: ToolBox append --source <daily.db> --target <history.duckdb>
/// </summary>
public class AppendTool : IToolCommand
{
    public string Name => "append";
    public string? Alias => "ap";
    public string Description => "追加每日 Bar 到历史 DuckDB（去重）";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<AppendService>();
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var sourcePath = "";
        var targetPath = "";

        for (int j = 0; j < args.Length; j++)
        {
            if (args[j] is "--source" or "-s" && j + 1 < args.Length) sourcePath = args[++j];
            else if (args[j] is "--target" or "-t" && j + 1 < args.Length) targetPath = args[++j];
        }

        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetPath))
        {
            Console.Error.WriteLine("Usage: ToolBox append --source <daily.duckdb> --target <history.duckdb>");
            return 1;
        }

        if (!File.Exists(sourcePath)) { Console.Error.WriteLine($"Source not found: {sourcePath}"); return 1; }
        if (!File.Exists(targetPath)) { Console.Error.WriteLine($"Target not found: {targetPath}"); return 1; }

        var log = sp.GetRequiredService<ILogger<AppendTool>>();
        var service = sp.GetRequiredService<AppendService>();

        log.LogInformation("Append: {Source} → {Target}", Path.GetFileName(sourcePath), Path.GetFileName(targetPath));

        var result = await service.RunAsync(sourcePath, targetPath, ct: ct);

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════");
        Console.WriteLine("  Append Result");
        Console.WriteLine("═══════════════════════════════════");
        foreach (var t in result.Tables)
            Console.WriteLine($"  [{t.Status}] {t.TableName}: +{t.RowsAppended:N0} new, skipped {t.RowsSkipped:N0}");
        Console.WriteLine($"  Total: +{result.TotalAppended:N0} appended, {result.TotalSkipped:N0} skipped");
        Console.WriteLine("═══════════════════════════════════");

        return result.TotalAppended >= 0 ? 0 : 1;
    }
}
