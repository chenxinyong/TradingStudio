using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingStudio.ToolBox.AnalyzeTool;

/// <summary>
/// 品种分析工具 — 基于 DuckDB 的品种排名、合约周期分类、研究报告。
///
/// 用法:
///   ToolBox analyze rank    [--db <path>] [--dim <volume|turnover|oi>] [--top 30] [--output <csv>]
///   ToolBox analyze classify [--db <path>] [--year 2025] [--output <csv>]
///   ToolBox analyze report   [--db <path>] [--top 30] [--output-dir <dir>]
/// </summary>
public class AnalyzeTool : IToolCommand
{
    public string Name => "analyze";
    public string? Alias => "a";
    public string Description => "品种排名、合约周期分类、研究报告";

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<AnalyzeService>();
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
            "rank" => await RunRankAsync(sp, rest, ct),
            "classify" => await RunClassifyAsync(sp, rest, ct),
            "report" => await RunReportAsync(sp, rest, ct),
            _ => UnknownSub(sub),
        };
    }

    // ──────────── helpers ────────────

    /// <summary>向上遍历目录树，查找 data/bars_history.duckdb</summary>
    private static string ResolveDbPath(string dbPath)
    {
        if (Path.IsPathRooted(dbPath)) return dbPath;
        if (File.Exists(dbPath)) return Path.GetFullPath(dbPath);

        // 从 exe 所在目录向上找（bin/Debug/net10.0 → repo root）
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, dbPath));
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        // fallback: cwd
        return Path.GetFullPath(dbPath);
    }

    // ──────────── rank ────────────

    private async Task<int> RunRankAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var db = ResolveDbPath("data/bars_history.duckdb");
        var dim = RankDimension.Volume;
        var top = 50;
        var output = "";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--db" or "-d" && i + 1 < args.Length) db = ResolveDbPath(args[++i]);
            else if (args[i] is "--dim" or "-m" && i + 1 < args.Length)
                dim = args[++i].ToLowerInvariant() switch
                {
                    "volume" or "vol" or "v" => RankDimension.Volume,
                    "turnover" or "to" => RankDimension.Turnover,
                    "openinterest" or "oi" => RankDimension.OpenInterest,
                    "barcount" or "bc" => RankDimension.BarCount,
                    "tradingdays" or "td" => RankDimension.TradingDays,
                    _ => RankDimension.Volume,
                };
            else if (args[i] is "--top" or "-t" && i + 1 < args.Length) top = int.Parse(args[++i]);
            else if (args[i] is "--output" or "-o" && i + 1 < args.Length) output = args[++i];
        }

        var log = sp.GetRequiredService<ILogger<AnalyzeTool>>();
        var svc = sp.GetRequiredService<AnalyzeService>();

        log.LogInformation("Ranking by {Dim}, top {N}, db={Db}", dim, top, db);
        var results = await svc.RankAsync(db, dim, top, ct);

        // 输出
        var sb = new StringBuilder();
        sb.AppendLine("Rank,Code,Name,BarCount,TotalVolume,TotalTurnover,AvgOI,TradingDays,ContractCount,FirstDate,LastDate");
        foreach (var r in results)
            sb.AppendLine($"{r.Rank},{r.Code},{r.RawCode},{r.BarCount},{r.TotalVolume:F0},{r.TotalTurnover:F0},{r.AvgOpenInterest:F0},{r.TradingDays},{r.ContractCount},{r.FirstDate},{r.LastDate}");

        if (!string.IsNullOrEmpty(output))
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(output, sb.ToString(), Encoding.UTF8, ct);
            log.LogInformation("Saved: {Path}", output);
        }

        PrintRankTable(results, dim);
        return 0;
    }

    // ──────────── classify ────────────

    private async Task<int> RunClassifyAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var db = ResolveDbPath("data/bars_history.duckdb");
        string? year = "2025";  // 默认按最近完整年度分类，避免六年汇总稀释 Top3
        var output = "";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--db" or "-d" && i + 1 < args.Length) db = ResolveDbPath(args[++i]);
            else if (args[i] is "--year" or "-y" && i + 1 < args.Length)
                year = args[++i] == "all" ? null : args[i];
            else if (args[i] is "--output" or "-o" && i + 1 < args.Length) output = args[++i];
        }

        var log = sp.GetRequiredService<ILogger<AnalyzeTool>>();
        var svc = sp.GetRequiredService<AnalyzeService>();

        var label = year ?? "all-years";
        log.LogInformation("Classifying contracts: {Year}, db={Db}", label, db);
        var results = await svc.ClassifyAsync(db, year, ct);

        // 汇总统计
        var summary = results.GroupBy(r => r.CycleType)
            .ToDictionary(g => g.Key, g => g.Count());
        log.LogInformation("Classification summary: {Summary}",
            string.Join(", ", summary.Select(kv => $"{kv.Key}={kv.Value}")));

        // CSV 输出
        var sb = new StringBuilder();
        sb.AppendLine("Code,RawCode,CycleType,ContractCount,Top1Share,Top3Share,Top1,Top2,Top3,ActiveMonths,TotalVolume");
        foreach (var r in results)
            sb.AppendLine($"{r.Code},{r.RawCode},{r.CycleType},{r.ContractCount},{r.Top1Share:F3},{r.Top3Share:F3},{r.Top1Contract},{r.Top2Contract},{r.Top3Contract},{r.ActiveMonths},{r.TotalVolume:F0}");

        if (!string.IsNullOrEmpty(output))
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(output, sb.ToString(), Encoding.UTF8, ct);
            log.LogInformation("Saved: {Path}", output);
        }

        PrintClassifyTable(results);
        return 0;
    }

    // ──────────── report ────────────

    private async Task<int> RunReportAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var db = ResolveDbPath("data/bars_history.duckdb");
        var top = 30;
        var outputDir = "docs/analysis";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--db" or "-d" && i + 1 < args.Length) db = ResolveDbPath(args[++i]);
            else if (args[i] is "--top" or "-t" && i + 1 < args.Length) top = int.Parse(args[++i]);
            else if (args[i] is "--output-dir" or "-o" && i + 1 < args.Length) outputDir = args[++i];
        }

        // 向上查找 repo root（有 docs/ 目录的那层）
        if (!Path.IsPathRooted(outputDir))
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                var candidate = Path.GetFullPath(Path.Combine(dir, outputDir));
                if (Directory.Exists(candidate) || Directory.Exists(Path.GetDirectoryName(candidate)!))
                { outputDir = candidate; break; }
                dir = Path.GetFullPath(Path.Combine(dir, ".."));
            }
            if (!Path.IsPathRooted(outputDir)) outputDir = Path.GetFullPath(outputDir);
        }
        if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

        var log = sp.GetRequiredService<ILogger<AnalyzeTool>>();
        var svc = sp.GetRequiredService<AnalyzeService>();

        log.LogInformation("Generating report: top {N}, db={Db} → {Dir}", top, db, outputDir);

        // 获取排名（全量）和分类（按 2025 年，避免六年汇总稀释）
        var ranks = await svc.RankAsync(db, RankDimension.Volume, top, ct);
        var cycles = await svc.ClassifyAsync(db, "2025", ct);
        var cycleMap = cycles.ToDictionary(c => c.Code, c => c);

        // 生成综合报告
        var date = DateTime.Now.ToString("yyyyMMdd");
        var reportPath = Path.Combine(outputDir, $"variety_report_{date}.md");
        var md = new StringBuilder();
        md.AppendLine($"# 期货品种深度研究报告");
        md.AppendLine();
        md.AppendLine($"> 生成日期：{DateTime.Now:yyyy-MM-dd} | 数据来源：2020-2025 六年全量 Bar | 引擎：DuckDB");
        md.AppendLine();

        // 排名总表
        md.AppendLine("## 一、Top 30 品种排名（六年总成交量）");
        md.AppendLine();
        md.AppendLine("| 排名 | 代码 | 品种 | 六年总成交量 | 总成交额 | 平均持仓 | 合约数 | 交易天数 | 数据范围 |");
        md.AppendLine("|------|------|------|-------------|---------|---------|--------|---------|----------|");
        foreach (var r in ranks)
        {
            var volStr = r.TotalVolume >= 1e9 ? $"{r.TotalVolume / 1e9:F1}亿" : $"{r.TotalVolume / 1e4:F0}万";
            md.AppendLine($"| {r.Rank} | {r.Code} | {r.RawCode} | {volStr} | {r.TotalTurnover / 1e8:F1}亿 | {r.AvgOpenInterest / 1e4:F0}万 | {r.ContractCount} | {r.TradingDays} | {r.FirstDate}~{r.LastDate} |");
        }
        md.AppendLine();

        // 合约周期分类
        md.AppendLine("## 二、合约周期分类");
        md.AppendLine();
        md.AppendLine("### 分类标准");
        md.AppendLine();
        md.AppendLine("| 类型 | 判断条件 |");
        md.AppendLine("|------|---------|");
        md.AppendLine("| 三主力轮换 | Top3 合约占比 > 70%，各有 > 10% |");
        md.AppendLine("| 多月活跃 | Top3 < 65% 且 ≥ 8 个活跃月份 |");
        md.AppendLine("| 偏主力集中 | Top3 在 50%-70% 之间 |");
        md.AppendLine("| 单合约主导 | 第一名 > 65% |");
        md.AppendLine("| 少合约(新品种) | 全年 ≤ 3 个合约 |");
        md.AppendLine();

        foreach (var group in new[] { "三主力轮换", "多月活跃", "偏主力集中", "单合约主导", "少合约(新品种)" })
        {
            var items = ranks.Where(r => cycleMap.TryGetValue(r.Code, out var c) && c.CycleType == group).ToList();
            if (items.Count == 0) continue;
            md.AppendLine($"### {group}（{items.Count} 个）");
            md.AppendLine();
            foreach (var r in items)
            {
                var c = cycleMap[r.Code];
                md.AppendLine($"- **{r.Code}** (排名 #{r.Rank}) — {svc.ClassifyDetail(c)}");
            }
            md.AppendLine();
        }

        // 品种详情（每个 Top 品种一段）
        md.AppendLine("## 三、Top 30 品种详情");
        md.AppendLine();
        foreach (var r in ranks)
        {
            var c = cycleMap.GetValueOrDefault(r.Code);
            md.AppendLine($"### {r.Code} — {r.RawCode}（排名 #{r.Rank}）");
            md.AppendLine();
            md.AppendLine($"| 维度 | 数值 |");
            md.AppendLine($"|------|------|");
            md.AppendLine($"| 六年总成交量 | {r.TotalVolume / 1e8:F1} 亿 |");
            md.AppendLine($"| 六年总成交额 | {r.TotalTurnover / 1e8:F1} 亿 |");
            md.AppendLine($"| 平均持仓量 | {r.AvgOpenInterest / 1e4:F1} 万 |");
            md.AppendLine($"| 合约数量 | {r.ContractCount} |");
            md.AppendLine($"| 交易天数 | {r.TradingDays} |");
            if (c != null)
            {
                md.AppendLine($"| 合约周期类型 | {c.CycleType} |");
                md.AppendLine($"| Top 3 合约 | {c.Top1Contract} / {c.Top2Contract} / {c.Top3Contract} |");
                md.AppendLine($"| Top 3 占比 | {c.Top3Share:P0} |");
            }
            md.AppendLine();
        }

        // 品类分布
        md.AppendLine("## 四、品类分布");
        md.AppendLine();
        md.AppendLine("| 品类 | 品种数 | 品种列表 |");
        md.AppendLine("|------|--------|----------|");
        // Simplified: group by leading code pattern
        var categories = new Dictionary<string, List<string>>();
        foreach (var r in ranks)
        {
            var cat = r.Code switch
            {
                "rb" or "hc" or "wr" or "i" or "j" or "jm" or "SM" or "SF" or "ss" => "黑色金属",
                "cu" or "al" or "zn" or "pb" or "ni" or "sn" => "有色金属",
                "au" or "ag" => "贵金属",
                "TA" or "MA" or "FG" or "SA" or "v" or "pp" or "l" or "eg" or "eb" or "bu" or "sp" or "ru" or "fu" or "PG" or "UR" => "化工能源",
                "m" or "RM" or "OI" or "p" or "y" or "c" or "cs" or "a" or "b" or "CF" or "SR" or "jd" or "lh" or "AP" or "CJ" or "PK" => "农产品",
                _ => "其他",
            };
            if (!categories.ContainsKey(cat)) categories[cat] = new();
            categories[cat].Add(r.Code);
        }
        foreach (var (cat, codes) in categories.OrderByDescending(kv => kv.Value.Count))
            md.AppendLine($"| {cat} | {codes.Count} | {string.Join(", ", codes)} |");

        await File.WriteAllTextAsync(reportPath, md.ToString(), Encoding.UTF8, ct);
        log.LogInformation("Report saved: {Path}", reportPath);

        Console.WriteLine();
        Console.WriteLine($"══ 报告已生成 ══");
        Console.WriteLine($"  路径: {reportPath}");
        Console.WriteLine($"  品种: Top {top}");

        return 0;
    }

    // ──────────── helpers ────────────

    private int UnknownSub(string sub)
    {
        Console.Error.WriteLine($"Unknown subcommand: {sub}");
        Console.Error.WriteLine("Available: rank, classify, report");
        return 1;
    }

    private void PrintRankTable(List<VarietyRank> results, RankDimension dim)
    {
        Console.WriteLine();
        Console.WriteLine($"══ 品种排名（{dim}）══");
        Console.WriteLine($"{"Rank",-5} {"Code",-6} {"Volume",-14} {"Turnover",-14} {"AvgOI",-10} {"Contracts",-10} {"TradingDays"}");
        Console.WriteLine(new string('─', 80));
        foreach (var r in results.Take(40))
        {
            var volStr = r.TotalVolume >= 1e9 ? $"{r.TotalVolume / 1e9:F1}亿" : $"{r.TotalVolume / 1e4:F0}万";
            var toStr = r.TotalTurnover >= 1e8 ? $"{r.TotalTurnover / 1e8:F1}亿" : $"{r.TotalTurnover / 1e4:F0}万";
            Console.WriteLine($"{r.Rank,-5} {r.Code,-6} {volStr,-14} {toStr,-14} {r.AvgOpenInterest / 1e4,8:F1}万  {r.ContractCount,-10} {r.TradingDays}");
        }
    }

    private void PrintClassifyTable(List<ContractCycleResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("══ 合约周期分类 ══");
        Console.WriteLine($"{"Code",-6} {"CycleType",-16} {"#Ctr",-5} {"Top1%",-7} {"Top3%",-7} {"Top1",-10} {"Top2",-10} {"Top3",-10}");
        Console.WriteLine(new string('─', 85));
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Code,-6} {r.CycleType,-16} {r.ContractCount,-5} {r.Top1Share,-7:P0} {r.Top3Share,-7:P0} {r.Top1Contract,-10} {r.Top2Contract,-10} {r.Top3Contract,-10}");
        }
    }

    private void PrintUsage()
    {
        Console.WriteLine("ToolBox analyze — 品种分析工具集");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  ToolBox analyze rank     [--db <path>] [--dim vol|to|oi] [--top 30] [--output <csv>]");
        Console.WriteLine("  ToolBox analyze classify [--db <path>] [--year 2025] [--output <csv>]");
        Console.WriteLine("  ToolBox analyze report   [--db <path>] [--top 30] [--output-dir <dir>]");
        Console.WriteLine();
        Console.WriteLine("子命令:");
        Console.WriteLine("  rank      — 多维度品种排名（成交量/成交额/持仓量/Bar数/交易天数）");
        Console.WriteLine("  classify  — 合约周期自动分类（三主力轮换/多月活跃/单合约主导/...）");
        Console.WriteLine("  report    — 生成综合研究报告 Markdown");
        Console.WriteLine();
        Console.WriteLine("示例:");
        Console.WriteLine("  ToolBox analyze rank");
        Console.WriteLine("  ToolBox analyze rank --dim turnover --top 30 -o data/research/ranking_turnover.csv");
        Console.WriteLine("  ToolBox analyze classify --year 2025");
        Console.WriteLine("  ToolBox analyze report --top 30");
    }
}
