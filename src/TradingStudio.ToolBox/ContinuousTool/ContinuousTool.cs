using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingStudio.ToolBox.ContinuousTool;

/// <summary>
/// 加权连续合约工具 — 为 Top 30 品种构建加权连续合约（日线级）。
///
/// 用法:
///   ToolBox continuous build  [--db <path>] [--variety rb] [--all-top30] [--output-dir <dir>]
///   ToolBox continuous info    [--db <path>] [--variety rb]
///   ToolBox continuous export  [--db <path>] [--variety rb] [--output <csv>] [--format csv|json]
/// </summary>
public class ContinuousTool : IToolCommand
{
    public string Name => "continuous";
    public string? Alias => "cont";
    public string Description => "加权连续合约构建/查询/导出";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ContinuousService>();
    }

    public async Task<int> ExecuteAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        var sub = args[0].ToLowerInvariant();
        var rest = args[1..];

        return sub switch
        {
            "build" => await RunBuildAsync(sp, rest, ct),
            "info" => await RunInfoAsync(sp, rest, ct),
            "export" => await RunExportAsync(sp, rest, ct),
            _ => UnknownSub(sub),
        };
    }

    // ──────────── build ────────────

    private async Task<int> RunBuildAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var db = ResolveDbPath("data/bars_history.duckdb");
        var variety = "";
        var allTop30 = false;
        var outputDir = "data/continuous";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--db" or "-d" && i + 1 < args.Length) db = ResolveDbPath(args[++i]);
            else if (args[i] is "--variety" or "-v" && i + 1 < args.Length) variety = args[++i];
            else if (args[i] is "--all-top30") allTop30 = true;
            else if (args[i] is "--output-dir" or "-o" && i + 1 < args.Length) outputDir = args[++i];
        }

        if (!allTop30 && string.IsNullOrEmpty(variety))
        {
            Console.Error.WriteLine("Specify --variety <code> or --all-top30");
            return 1;
        }

        outputDir = ResolveOutputDir(outputDir);
        if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

        var log = sp.GetRequiredService<ILogger<ContinuousTool>>();
        var svc = sp.GetRequiredService<ContinuousService>();

        var varieties = allTop30
            ? GetTop30Codes(db).OrderBy(c => c).ToList()
            : new List<string> { variety };

        log.LogInformation("Building continuous for {Count} varieties → {Dir}", varieties.Count, outputDir);

        int success = 0, fail = 0;
        foreach (var v in varieties)
        {
            try
            {
                var result = await svc.BuildAsync(db, v, outputDir, ct);
                if (result.AdjustedDays.Count > 0)
                {
                    success++;
                    Console.WriteLine($"[OK] {v}: {result.AdjustedDays.Count}d day, {result.Weighted1MinCount:N0} 1min, {result.Rollovers.Count} rollovers → {result.OutputPath}");
                }
                else
                {
                    fail++;
                    Console.WriteLine($"[SKIP] {v}: no data");
                }
            }
            catch (Exception ex)
            {
                fail++;
                log.LogError(ex, "Failed to build continuous for {Variety}", v);
                Console.WriteLine($"[FAIL] {v}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Done: {success} OK, {fail} failed");
        return fail > 0 ? 1 : 0;
    }

    // ──────────── info ────────────

    private async Task<int> RunInfoAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var db = "";
        var variety = "";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--db" or "-d" && i + 1 < args.Length) db = args[++i];
            else if (args[i] is "--variety" or "-v" && i + 1 < args.Length) variety = args[++i];
        }

        if (string.IsNullOrEmpty(variety))
        {
            Console.Error.WriteLine("Specify --variety <code> (e.g. rb)");
            return 1;
        }

        // Try to find the continuous DB
        var infoDir = ResolveOutputDir("data/continuous");
        var contPath = Path.Combine(infoDir, $"{variety}_continuous.duckdb");
        if (string.IsNullOrEmpty(contPath))
        {
            Console.Error.WriteLine($"No continuous DB found for {variety}. Run 'continuous build --variety {variety}' first.");
            return 1;
        }

        using var duck = new DuckDBConnection($"Data Source={contPath}");
        await duck.OpenAsync(ct);

        // Summary
        using (var cmd = duck.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), MIN(trading_day), MAX(trading_day) FROM continuous_day";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                Console.WriteLine($"══ {variety} 加权连续合约 ══");
                Console.WriteLine($"  Days:      {r.GetInt64(0):N0}");
                Console.WriteLine($"  Range:     {r.GetString(1)} ~ {r.GetString(2)}");
            }
        }

        // Rollovers
        Console.WriteLine();
        using (var cmd = duck.CreateCommand())
        {
            cmd.CommandText = "SELECT rollover_date, old_dominant, new_dominant, ROUND(price_ratio,6), ROUND(old_close/1e7,1), ROUND(new_close/1e7,1) FROM rollover_log ORDER BY rollover_date";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            bool hasData = false;
            while (await r.ReadAsync(ct))
            {
                if (!hasData) { Console.WriteLine("  换月记录:"); hasData = true; }
                Console.WriteLine($"    {r.GetString(0)}  {r.GetString(1)} → {r.GetString(2)}  ratio={r.GetDouble(3):F6}  {r.GetDouble(4):F0}→{r.GetDouble(5):F0}");
            }
            if (!hasData) Console.WriteLine("  (无换月记录)");
        }

        // Last few days
        Console.WriteLine();
        Console.WriteLine("  Recent:");
        using (var cmd = duck.CreateCommand())
        {
            cmd.CommandText = "SELECT trading_day, ROUND(close/1e7,1), ROUND(volume/1e4,0), dominant_contract, ROUND(adjustment_factor,4) FROM continuous_day ORDER BY trading_day DESC LIMIT 5";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                Console.WriteLine($"    {r.GetString(0)}  close={r.GetDouble(1),8:F1}  vol={r.GetDouble(2),8:F0}万  dom={r.GetString(3)}  adj={r.GetDouble(4):F4}");
            }
        }

        return 0;
    }

    // ──────────── export ────────────

    private async Task<int> RunExportAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var variety = "";
        var output = "";
        var format = "csv";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--variety" or "-v" && i + 1 < args.Length) variety = args[++i];
            else if (args[i] is "--output" or "-o" && i + 1 < args.Length) output = args[++i];
            else if (args[i] is "--format" or "-f" && i + 1 < args.Length) format = args[++i];
        }

        if (string.IsNullOrEmpty(variety))
        {
            Console.Error.WriteLine("Specify --variety <code>");
            return 1;
        }

        var contPath = Path.Combine(ResolveOutputDir("data/continuous"), $"{variety}_continuous.duckdb");
        if (!File.Exists(contPath))
        {
            Console.Error.WriteLine($"No continuous DB for {variety}");
            return 1;
        }

        if (string.IsNullOrEmpty(output))
            output = $"data/continuous/{variety}_export.csv";

        if (!Path.IsPathRooted(output))
            output = Path.GetFullPath(output);

        using var duck = new DuckDBConnection($"Data Source={contPath}");
        await duck.OpenAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("trading_day,open,high,low,close,volume,dominant_contract,adjustment_factor,raw_close");

        using var cmd = duck.CreateCommand();
        cmd.CommandText = "SELECT trading_day,open,high,low,close,volume,dominant_contract,adjustment_factor,raw_close FROM continuous_day ORDER BY trading_day";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            sb.AppendLine($"{r.GetString(0)},{r.GetDouble(1):F0},{r.GetDouble(2):F0},{r.GetDouble(3):F0},{r.GetDouble(4):F0},{r.GetDouble(5):F0},{r.GetString(6)},{r.GetDouble(7):F6},{r.GetDouble(8):F0}");
        }

        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(output, sb.ToString(), Encoding.UTF8, ct);

        Console.WriteLine($"Exported to: {output}");
        return 0;
    }

    // ──────────── helpers ────────────

    private static string ResolveDbPath(string dbPath)
    {
        if (Path.IsPathRooted(dbPath) && File.Exists(dbPath)) return dbPath;
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, dbPath));
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        return Path.GetFullPath(dbPath);
    }

    /// <summary>向上查找 repo root 中的目录</summary>
    private static string ResolveOutputDir(string relativeDir)
    {
        if (Path.IsPathRooted(relativeDir)) return relativeDir;
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, relativeDir));
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        return Path.GetFullPath(relativeDir);
    }

    private static List<string> GetTop30Codes(string dbPath)
    {
        // 硬编码 Top 30（从 Phase 3 分析确认）
        return new List<string> {
            "rb","ta","m","ma","sa","fg","fu","ag","p","v",
            "rm","i","y","c","hc","pp","oi","bu","sr","cf",
            "l","ru","ni","eg","eb","sp","al","jm","sm","zn"
        };
    }

    private int UnknownSub(string sub)
    {
        Console.Error.WriteLine($"Unknown subcommand: {sub}");
        Console.Error.WriteLine("Available: build, info, export");
        return 1;
    }

    private void PrintUsage()
    {
        Console.WriteLine("ToolBox continuous — 加权连续合约工具");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  ToolBox continuous build   [--variety rb] [--all-top30] [--output-dir <dir>]");
        Console.WriteLine("  ToolBox continuous info    [--variety rb]");
        Console.WriteLine("  ToolBox continuous export  [--variety rb] [--output <csv>]");
        Console.WriteLine();
        Console.WriteLine("子命令:");
        Console.WriteLine("  build   — 构建加权连续合约（50/50 vol+oi 等权，比率回补）");
        Console.WriteLine("  info    — 查看连续合约概要（交易日范围、换月记录）");
        Console.WriteLine("  export  — 导出连续合约 CSV");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  ToolBox continuous build --variety rb");
        Console.WriteLine("  ToolBox continuous build --all-top30");
        Console.WriteLine("  ToolBox continuous info --variety rb");
        Console.WriteLine("  ToolBox continuous export --variety rb -o rb_continuous.csv");
    }
}
