using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TradingStudio.Core.Analysis;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Engine;

namespace TradingStudio.Commands;

/// <summary>
/// 单参数敏感度扫描 CLI 命令。
///
/// 用法:
///   TradingStudio param-scan --config &lt;strategy.json&gt; --param &lt;ParamName&gt; [--db &lt;path&gt;] [--symbols &lt;path&gt;] [--start &lt;date&gt;] [--end &lt;date&gt;] [--steps &lt;N&gt;]
///
/// 与 walkforward 的区别:
///   - 只扫描指定的一个参数（而非全部可优化参数）
///   - 更高粒度（默认 20 步，walkforward 上限 10 步）
///   - 跳过 Walk-Forward 阶段，更快速
///   - 可指定数据范围缩小扫描时间
///
/// 参考: Ernie Chan《Algorithmic Trading》Ch3 — 参数高原 vs 尖峰
/// </summary>
public class ParamScanCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var configPath = "";
        var paramName = "";
        var dbPath = ResolveHistoryDb();
        var symbolsPath = "symbols.json";
        var startStr = "";
        var endStr = "";
        var steps = 20;
        double? minOverride = null, maxOverride = null, stepOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" or "-c" && i + 1 < args.Length) configPath = args[++i];
            else if (args[i] is "--param" or "-p" && i + 1 < args.Length) paramName = args[++i];
            else if (args[i] is "--db" or "-d" && i + 1 < args.Length) dbPath = args[++i];
            else if (args[i] is "--symbols" && i + 1 < args.Length) symbolsPath = args[++i];
            else if (args[i] is "--start" && i + 1 < args.Length) startStr = args[++i];
            else if (args[i] is "--end" && i + 1 < args.Length) endStr = args[++i];
            else if (args[i] is "--steps" && i + 1 < args.Length) steps = int.Parse(args[++i]);
            else if (args[i] is "--min" && i + 1 < args.Length) minOverride = double.Parse(args[++i]);
            else if (args[i] is "--max" && i + 1 < args.Length) maxOverride = double.Parse(args[++i]);
            else if (args[i] is "--step" && i + 1 < args.Length) stepOverride = double.Parse(args[++i]);
        }

        if (string.IsNullOrEmpty(configPath) || string.IsNullOrEmpty(paramName))
        {
            Console.Error.WriteLine("Usage: TradingStudio param-scan --config <strategy.json> --param <ParamName> [--db <path>] [--steps <N>] [--min <v> --max <v> --step <v>]");
            Console.Error.WriteLine("  Default DB: data/bars_history.duckdb (auto-detected)");
            Console.Error.WriteLine("  Default steps: 20");
            Console.Error.WriteLine("  --min/--max/--step: 覆盖参数扫描范围 (聚焦子区间重跑, 如 --min 150 --max 500 --step 20)");
            return 1;
        }

        if (!File.Exists(configPath)) { Console.Error.WriteLine($"Config not found: {configPath}"); return 1; }
        if (!File.Exists(dbPath)) { Console.Error.WriteLine($"DB not found: {dbPath}"); return 1; }

        // symbols.json
        if (!File.Exists(symbolsPath))
        {
            var alt = Path.Combine(AppContext.BaseDirectory, symbolsPath);
            if (File.Exists(alt)) symbolsPath = alt;
        }

        var json = File.ReadAllText(configPath);
        var baseConfig = JsonSerializer.Deserialize<StrategyConfig>(json, JsonOpts);
        if (baseConfig == null) { Console.Error.WriteLine("Failed to parse config"); return 1; }

        // 预热
        StrategyFactory.DiscoverFromAssembly(typeof(TradingEngine).Assembly);
        StrategyFactory.DiscoverFromAssembly(typeof(TradingStudio.Strategy.ChanLunStrategy).Assembly);

        // 获取指定参数的 OptimizeRange
        var strategy = StrategyFactory.Create(baseConfig);
        var metas = StrategyFactory.GetOptimizableParams(strategy);
        var meta = metas.FirstOrDefault(m => m.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase));
        if (meta == null)
        {
            Console.Error.WriteLine($"Parameter '{paramName}' not found in strategy '{baseConfig.StrategyType}'.");
            Console.Error.WriteLine($"Available: {string.Join(", ", metas.Select(m => $"{m.Name}[{m.Min}..{m.Max}/{m.Step}]"))}");
            return 1;
        }

        // 可选覆盖扫描范围（如 --min 150 --max 500 --step 20），用于聚焦子区间重跑
        if (minOverride.HasValue || maxOverride.HasValue || stepOverride.HasValue)
        {
            meta = new ParamMeta
            {
                Name = meta.Name,
                Group = meta.Group,
                Min = minOverride ?? meta.Min,
                Max = maxOverride ?? meta.Max,
                Step = stepOverride ?? meta.Step,
                ValueType = meta.ValueType,
            };
        }

        Console.WriteLine($"\n═══ Parameter Sensitivity Scan ═══");
        Console.WriteLine($"  Config:    {Path.GetFileName(configPath)}");
        Console.WriteLine($"  Strategy:  {baseConfig.StrategyType} ({baseConfig.StrategyId})");
        Console.WriteLine($"  Parameter: {meta.Name} [{meta.Min} → {meta.Max}, step={meta.Step}]");
        Console.WriteLine($"  DB:        {Path.GetFileName(dbPath)}");
        Console.WriteLine($"  Instruments: {string.Join(", ", baseConfig.Instruments)}");
        Console.WriteLine();

        // 生成扫描值 — 更高粒度（不超过 steps 个点）
        var scanValues = GenerateScanValues(meta, steps);
        Console.WriteLine($"  Scan points: {scanValues.Length} ({scanValues[0]} → {scanValues[^1]})");
        Console.WriteLine();

        // 日期范围
        DateTime? dataStart = string.IsNullOrEmpty(startStr) ? baseConfig.DataStartDate : DateTime.Parse(startStr);
        DateTime? dataEnd = string.IsNullOrEmpty(endStr) ? baseConfig.DataEndDate : DateTime.Parse(endStr);

        // ── 扫描 ──
        var analyzer = new ParameterStabilityAnalyzer();
        var scanResults = new List<(double ParamValue, double Sharpe)>();
        var swTotal = Stopwatch.StartNew();
        int completed = 0, errors = 0;

        foreach (var val in scanValues)
        {
            completed++;
            Console.Write($"  [{completed}/{scanValues.Length}] {meta.Name}={val,8:F1} ... ");
            var sw = Stopwatch.StartNew();

            try
            {
                var modifiedConfig = WalkForwardCommand.CloneWithParameter(baseConfig, meta.Name, val);
                var perf = await WalkForwardCommand.RunSingleBacktest(
                    modifiedConfig, dbPath, symbolsPath,
                    dataStart, dataEnd,
                    $"ps-{meta.Name}-{val}");
                scanResults.Add((val, (double)perf.SharpeRatio));
                sw.Stop();
                Console.WriteLine($"Sharpe={perf.SharpeRatio,7:F3}  PnL={perf.TotalNetProfit,10:C}  T={perf.TotalTrades,4}  {sw.Elapsed.TotalSeconds,5:F0}s");
            }
            catch (Exception ex)
            {
                errors++;
                sw.Stop();
                Console.WriteLine($"ERR: {ex.Message}  {sw.Elapsed.TotalSeconds:F0}s");
            }
        }

        swTotal.Stop();

        // ── 分析 ──
        Console.WriteLine();
        if (scanResults.Count < 3)
        {
            Console.WriteLine($"  ⚠️  Only {scanResults.Count} valid results — insufficient for stability analysis.");
            return 1;
        }

        var report = analyzer.AnalyzeSingleParameter(meta.Name, scanResults);

        // ── 生成报告 ──
        var reportPath = Path.ChangeExtension(configPath, $".param-scan-{meta.Name}.md");
        var md = BuildMarkdownReport(baseConfig, meta, scanValues, scanResults, report, swTotal.Elapsed);
        await File.WriteAllTextAsync(reportPath, md, Encoding.UTF8);

        Console.WriteLine(md);
        Console.WriteLine($"  Report saved: {reportPath}");
        Console.WriteLine($"  Total time: {swTotal.Elapsed.TotalMinutes:F1} min, {errors} errors");

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Scan Value Generation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 生成参数扫描值 — 比 WalkForwardCommand 的 GenerateScanValues 更高粒度。
    /// 目标：均匀覆盖参数范围，最多 maxSteps 个点。
    /// </summary>
    private static double[] GenerateScanValues(ParamMeta meta, int maxSteps)
    {
        if (meta.Step <= 0 || meta.Min >= meta.Max)
            return Array.Empty<double>();

        var range = meta.Max - meta.Min;
        var rawCount = range / meta.Step;

        // 如果原始步数 ≤ maxSteps，使用原始步长
        double effectiveStep;
        if (rawCount <= maxSteps)
        {
            effectiveStep = meta.Step;
        }
        else
        {
            // 放大步长以符合 maxSteps
            effectiveStep = range / (maxSteps - 1);
            if (meta.ValueType == "Int32")
                effectiveStep = Math.Max(1, Math.Round(effectiveStep));
        }

        var list = new List<double>();
        for (double v = meta.Min; v <= meta.Max + effectiveStep * 0.1; v += effectiveStep)
        {
            var val = meta.ValueType == "Int32" ? Math.Round(v) : Math.Round(v, 6);
            if (val < meta.Min || val > meta.Max) continue;
            // 去重
            if (list.Count > 0 && Math.Abs(list[^1] - val) < effectiveStep * 0.01) continue;
            list.Add(val);
        }

        return list.ToArray();
    }

    // ═══════════════════════════════════════════════════════════════
    // Markdown Report
    // ═══════════════════════════════════════════════════════════════

    private static string BuildMarkdownReport(
        StrategyConfig config, ParamMeta meta,
        double[] scanValues,
        List<(double ParamValue, double Sharpe)> results,
        ParameterStabilityReport report,
        TimeSpan elapsed)
    {
        var sb = new StringBuilder();

        var verdictIcon = report.Verdict switch
        {
            StabilityVerdict.Robust => "✅",
            StabilityVerdict.Acceptable => "⚠️",
            StabilityVerdict.Fragile => "🔴",
            StabilityVerdict.Overfit => "❌",
            _ => "❓"
        };

        sb.AppendLine($"# `{meta.Name}` 参数敏感度分析 {verdictIcon}");
        sb.AppendLine();
        sb.AppendLine($"**策略**: {config.StrategyType} (`{config.StrategyId}`)");
        sb.AppendLine($"**品种**: {string.Join(", ", config.Instruments)}");
        sb.AppendLine($"**参数**: {meta.Name} ({meta.Group}) — 默认值 `{meta.Min + meta.Max / 2:F0}`, 范围 `{meta.Min} → {meta.Max}`");
        sb.AppendLine($"**扫描点数**: {scanValues.Length} | **耗时**: {elapsed.TotalMinutes:F1} min");
        sb.AppendLine($"**生成时间**: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        // ── 结论 ──
        sb.AppendLine("---");
        sb.AppendLine("## 结论");
        sb.AppendLine();
        sb.AppendLine(report.ToMarkdown());
        sb.AppendLine();

        // ── 扫描数据 ──
        sb.AppendLine("---");
        sb.AppendLine("## 扫描数据");
        sb.AppendLine();
        sb.AppendLine("| # | {meta.Name} | Sharpe | PnL 方向 |");
        sb.AppendLine("|---|:---:|:---:|:---:|");

        var sorted = results.OrderBy(r => r.ParamValue).ToList();
        var bestIdx = sorted.IndexOf(sorted.MaxBy(r => r.Sharpe));

        for (int i = 0; i < sorted.Count; i++)
        {
            var (val, sharpe) = sorted[i];
            var marker = i == bestIdx ? " ⭐" : "";
            var bar = SharpeBar(sharpe);
            sb.AppendLine($"| {i + 1} | {val:F1} | {sharpe:F3}{marker} | {bar} |");
        }
        sb.AppendLine();
        sb.AppendLine($"⭐ = 最优参数 (Sharpe = {sorted[bestIdx].Sharpe:F3})");
        sb.AppendLine();

        // ── Sharpe × Param 可视化 ──
        sb.AppendLine("### Sharpe 曲线");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(AsciiChart(sorted, report));
        sb.AppendLine("```");
        sb.AppendLine();

        // ── 高原分析 ──
        if (report.PlateauWidth > 0)
        {
            sb.AppendLine("### 参数高原");
            sb.AppendLine();
            sb.AppendLine($"- **高原区间**: `{report.PlateauStart:F1}` → `{report.PlateauEnd:F1}` (宽度 {report.PlateauWidth:F1}, 占比 {report.PlateauWidthRatio:P1})");
            sb.AppendLine($"- **高原内 Sharpe ≥ {report.MaxSharpe * 0.9:F3}** (maxSharpe 的 90%)");
            sb.AppendLine($"- **含义**: 参数在高原内任意取值，策略表现均在最优值的 10% 以内 — 不必精确拟合到单一最优值");
            sb.AppendLine();
        }

        // ── 实用建议 ──
        sb.AppendLine("---");
        sb.AppendLine("## 实用建议");
        sb.AppendLine();

        if (report.Verdict == StabilityVerdict.Robust || report.Verdict == StabilityVerdict.Acceptable)
        {
            sb.AppendLine($"- ✅ 参数 `{meta.Name}` 表现稳健，建议取高原中位数 `{(report.PlateauStart + report.PlateauEnd) / 2:F0}`");
            sb.AppendLine($"- 实盘中可在高原范围内 (±{report.PlateauWidth / 2:F0}) 微调，预计影响可控");
        }
        else
        {
            sb.AppendLine($"- ⚠️ 参数 `{meta.Name}` 对策略表现敏感，建议:");
            sb.AppendLine($"  1. 用 Walk-Forward 验证不同时期的最优值是否一致");
            sb.AppendLine($"  2. 考虑使用参数集组合（取多个候选值的信号投票）");
            sb.AppendLine($"  3. 放宽该参数依赖 — 增加其他独立信号源");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"*报告由 ParamScanCommand 自动生成。参考: Ernie Chan《Algorithmic Trading》Ch3。*");

        return sb.ToString();
    }

    /// <summary>Sharpe 值 → ASCII 条形图</summary>
    private static string SharpeBar(double sharpe)
    {
        if (double.IsNaN(sharpe)) return "N/A";

        var len = (int)Math.Round(Math.Abs(sharpe) * 10);
        len = Math.Clamp(len, 0, 30);
        var ch = sharpe >= 0 ? "█" : "▁";
        return new string(ch[0], len) + (sharpe >= 0 ? " +" : " -");
    }

    /// <summary>简易 ASCII 曲线图 — Sharpe vs Param</summary>
    private static string AsciiChart(List<(double ParamValue, double Sharpe)> sorted, ParameterStabilityReport report)
    {
        if (sorted.Count == 0) return "(no data)";

        var maxS = sorted.Max(r => r.Sharpe);
        var minS = sorted.Min(r => r.Sharpe);
        var range = maxS - minS;
        if (range == 0) range = 0.01;

        const int height = 12;
        const int width = 60;
        var paramMin = sorted.First().ParamValue;
        var paramMax = sorted.Last().ParamValue;
        var paramRange = paramMax - paramMin;
        if (paramRange == 0) paramRange = 1;

        // 构建网格
        var grid = new char[height, width];
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
                grid[r, c] = ' ';

        // 画数据点
        foreach (var (pv, s) in sorted)
        {
            var col = (int)((pv - paramMin) / paramRange * (width - 1));
            var row = (int)((maxS - s) / range * (height - 1));
            col = Math.Clamp(col, 0, width - 1);
            row = Math.Clamp(row, 0, height - 1);
            grid[row, col] = '●';
        }

        // 高原标记线
        if (report.PlateauWidth > 0)
        {
            var platStartCol = (int)((report.PlateauStart - paramMin) / paramRange * (width - 1));
            var platEndCol = (int)((report.PlateauEnd - paramMin) / paramRange * (width - 1));
            platStartCol = Math.Clamp(platStartCol, 0, width - 1);
            platEndCol = Math.Clamp(platEndCol, 0, width - 1);
            for (int c = platStartCol; c <= platEndCol; c++)
            {
                if (grid[0, c] == ' ') grid[0, c] = '─';
                if (grid[height - 1, c] == ' ') grid[height - 1, c] = '─';
            }
            if (platStartCol > 0) { grid[0, platStartCol] = '┌'; grid[height - 1, platStartCol] = '└'; }
            if (platEndCol < width - 1) { grid[0, platEndCol] = '┐'; grid[height - 1, platEndCol] = '┘'; }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Sharpe  [{maxS:F2}]");
        for (int r = 0; r < height; r++)
        {
            sb.Append(r == height / 2 ? "      " : "      ");
            for (int c = 0; c < width; c++)
                sb.Append(grid[r, c]);
            sb.AppendLine();
        }
        sb.AppendLine($"       [{minS:F2}]");
        sb.AppendLine($"       {paramMin:F0}{new string(' ', Math.Max(0, width - paramMin.ToString("F0").Length - paramMax.ToString("F0").Length))}{paramMax:F0}  Param");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // DB Path Resolution
    // ═══════════════════════════════════════════════════════════════

    private static string ResolveHistoryDb()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "data", "bars_history.duckdb"),
            Path.Combine(AppContext.BaseDirectory, "data", "bars_history.duckdb"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "bars_history.duckdb"),
            "bars_history.duckdb",
        ];
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return Path.GetFullPath(candidates[0]);
    }
}
