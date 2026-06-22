using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingStudio.Data.Storage;

namespace TradingStudio.ToolBox.BuildPeriodsTool;

/// <summary>
/// 多周期聚合 — 从 bars_1min 构建 5min/15min/day/week 连续合约表
/// 用法: ToolBox build-periods --db <history.duckdb> [--full]
/// </summary>
public class BuildPeriodsTool : IToolCommand
{
    public string Name => "build-periods";
    public string? Alias => "bp";
    public string Description => "构建 5min/15min/day/week 多周期表（连续合约）";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<BuildPeriodsService>();
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var dbPath = "";
        var fullRebuild = false;

        for (int j = 0; j < args.Length; j++)
        {
            if (args[j] is "--db" or "-d" && j + 1 < args.Length) dbPath = args[++j];
            else if (args[j] is "--full") fullRebuild = true;
        }

        if (string.IsNullOrEmpty(dbPath))
        {
            Console.Error.WriteLine("Usage: ToolBox build-periods --db <history.duckdb> [--full]");
            return 1;
        }

        if (!File.Exists(dbPath)) { Console.Error.WriteLine($"DB not found: {dbPath}"); return 1; }

        var log = sp.GetRequiredService<ILogger<BuildPeriodsTool>>();
        var service = sp.GetRequiredService<BuildPeriodsService>();

        log.LogInformation("Build periods: {Db} (mode: {Mode})",
            Path.GetFileName(dbPath), fullRebuild ? "full" : "incremental");

        var result = await service.RunAsync(dbPath, fullRebuild, ct);

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════");
        Console.WriteLine("  Multi-Period Build Result");
        Console.WriteLine("═══════════════════════════════════");
        foreach (var (table, rows) in result.Tables.OrderBy(x => x.Key))
            Console.WriteLine($"  {table}: {rows:N0} rows");
        if (result.Errors.Count > 0)
        {
            Console.WriteLine("  --- Errors ---");
            foreach (var e in result.Errors) Console.WriteLine($"  {e}");
        }
        Console.WriteLine($"  Total: {result.TotalRows:N0} rows");
        Console.WriteLine("═══════════════════════════════════");

        return result.Errors.Count == 0 ? 0 : 1;
    }
}
