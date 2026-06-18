using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingStudio.Tests")]

namespace TradingStudio.ToolBox.MergeTool;

/// <summary>
/// 数据合并工具 — 将多个 per-year SQLite DB 合并为单个 DuckDB。
///
/// 用法:
///   ToolBox merge [--source-dir data/] [--output data/bars_merged.duckdb] [--tables bars_1min,bars_day]
///
/// 示例:
///   ToolBox merge
///   ToolBox merge --source-dir ./data --output ./merged/bars.duckdb
///   ToolBox merge --tables bars_1min                   # 仅合并 1min 表
/// </summary>
public class MergeTool : IToolCommand
{
    public string Name => "merge";
    public string? Alias => "m";
    public string Description => "合并 per-year SQLite → 单个 DuckDB";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<MergeService>();
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var sourceDir = "data";
        var outputPath = "data/bars_merged.duckdb";
        var tables = new[] { "bars_1min", "bars_day" };

        // 参数解析
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--source-dir" or "-s" && i + 1 < args.Length)
                sourceDir = args[++i];
            else if (args[i] is "--output" or "-o" && i + 1 < args.Length)
                outputPath = args[++i];
            else if (args[i] is "--tables" or "-t" && i + 1 < args.Length)
                tables = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            else if (args[i] is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }
        }

        // 路径解析（相对 → 绝对）
        if (!Path.IsPathRooted(sourceDir))
            sourceDir = Path.GetFullPath(sourceDir);
        if (!Path.IsPathRooted(outputPath))
            outputPath = Path.GetFullPath(outputPath);

        // 确保输出目录存在
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        var log = sp.GetRequiredService<ILogger<MergeTool>>();
        log.LogInformation("Merge started: {SourceDir} → {Output}", sourceDir, outputPath);
        log.LogInformation("Tables: {Tables}", string.Join(", ", tables));

        var service = sp.GetRequiredService<MergeService>();
        var result = await service.RunAsync(sourceDir, outputPath, tables, ct);

        // 打印摘要
        PrintSummary(result);

        return result.TotalRows > 0 ? 0 : 1;
    }

    private void PrintSummary(MergeResult result)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════");
        Console.WriteLine("  Merge Complete");
        Console.WriteLine("═══════════════════════════════════════");
        Console.WriteLine($"  Output:   {result.OutputPath}");
        Console.WriteLine($"  Size:     {result.OutputSizeBytes / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"  Total:    {result.TotalRows:N0} rows");
        Console.WriteLine("───────────────────────────────────────");
        foreach (var t in result.Tables)
        {
            Console.WriteLine($"  {t.TableName,-18} {t.TotalRows,14:N0} rows");
        }
        Console.WriteLine("═══════════════════════════════════════");
    }

    private void PrintUsage()
    {
        Console.WriteLine("ToolBox merge — 合并 per-year SQLite → 单个 DuckDB");
        Console.WriteLine();
        Console.WriteLine("用法: ToolBox merge [options]");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  --source-dir, -s  <dir>  源 SQLite 目录 (默认 data/)");
        Console.WriteLine("  --output,     -o  <path> 输出 DuckDB 路径 (默认 data/bars_merged.duckdb)");
        Console.WriteLine("  --tables,     -t  <list> 合并表名，逗号分隔 (默认 bars_1min,bars_day)");
        Console.WriteLine("  --help,       -h         显示此帮助");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  ToolBox merge");
        Console.WriteLine("  ToolBox merge -s ./data -o ./merged/bars.duckdb");
        Console.WriteLine("  ToolBox merge -t bars_1min");
    }
}
