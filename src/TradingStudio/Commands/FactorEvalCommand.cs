using System.Text;
using TradingStudio.Core.Analysis;

namespace TradingStudio.Commands;

/// <summary>
/// C# 原生因子IC评估 — 从 Parquet 加载因子面板, 运行 Rank IC + Quantile 分析。
/// 替代 Python factor_ic.py / factor_quantile.py, 统一技术栈。
///
/// 用法: dotnet run -- factor-eval --panel <path> [--factor IntradayMom,VWAP_Dev]
/// </summary>
public class FactorEvalCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var panelPath = "";
        var factors = new List<string>();
        var isEnd = DateTime.Parse("2023-12-31");
        var outputPath = "";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--panel" or "-p" && i + 1 < args.Length) panelPath = args[++i];
            else if (args[i] is "--factors" or "-f" && i + 1 < args.Length)
                factors.AddRange(args[++i].Split(','));
            else if (args[i] is "--is-end" && i + 1 < args.Length) isEnd = DateTime.Parse(args[++i]);
            else if (args[i] is "--output" or "-o" && i + 1 < args.Length) outputPath = args[++i];
        }

        if (string.IsNullOrEmpty(panelPath))
        {
            Console.Error.WriteLine("Usage: TradingStudio factor-eval --panel <factors.parquet> [--factors f1,f2]");
            return 1;
        }

        if (!File.Exists(panelPath))
        {
            Console.Error.WriteLine($"Panel not found: {panelPath}");
            return 1;
        }

        Console.WriteLine($"═══ C# Factor IC Evaluation ═══");
        Console.WriteLine($"  Panel: {panelPath}");

        // Load panel via Python interop (Parquet → DataFrame → FactorSnapshot)
        var snapshots = await LoadPanelFromParquet(panelPath, factors);
        if (snapshots.Count == 0)
        {
            Console.Error.WriteLine("No data loaded. Make sure the panel has instrument_id/trading_day columns.");
            return 1;
        }

        Console.WriteLine($"  Loaded: {snapshots.Sum(s => s.FactorValues.Count):N0} factor values across {snapshots.Count} days");

        var evaluator = new FactorEvaluator();

        // Get factor names from first snapshot
        var factorNames = snapshots.First().FactorValues.Keys.ToList();
        if (factors.Count > 0) factorNames = factorNames.Where(f => factors.Contains(f)).ToList();

        Console.WriteLine($"  Factors: {string.Join(", ", factorNames)}");
        Console.WriteLine();

        var sb = new StringBuilder();
        sb.AppendLine($"# C# Factor IC Evaluation Report");
        sb.AppendLine($"> Panel: {Path.GetFileName(panelPath)}");
        sb.AppendLine();

        foreach (var factorName in factorNames)
        {
            // Build per-factor snapshots
            var factorSnaps = snapshots.Select(s =>
            {
                var fv = new Dictionary<string, double>();
                var fr = new Dictionary<string, double>();
                if (s.FactorValues.TryGetValue(factorName, out var val))
                    fv["_"] = val;
                if (s.ForwardReturns.TryGetValue(factorName, out var ret))
                    fr["_"] = ret;
                return new FactorSnapshot { Timestamp = s.Timestamp, FactorValues = fv, ForwardReturns = fr };
            }).Where(s => s.FactorValues.Count > 0 && s.ForwardReturns.Count > 0).ToList();

            if (factorSnaps.Count < 20) continue;

            // IC Analysis
            var icResult = evaluator.AnalyzeIC(factorSnaps);
            Console.WriteLine($"  {factorName}:");
            Console.WriteLine($"    Mean IC:   {icResult.MeanIC,10:F4}");
            Console.WriteLine($"    IC Std:    {icResult.StdIC,10:F4}");
            Console.WriteLine($"    IC IR:     {icResult.ICIR,10:F4}");
            Console.WriteLine($"    t-stat:    {icResult.TStatistic,10:F2}");
            Console.WriteLine($"    Pos Ratio: {icResult.PositiveRatio,10:P1}");
            Console.WriteLine($"    Verdict:   {icResult.Verdict}");

            sb.AppendLine($"## {factorName}");
            sb.AppendLine($"| 指标 | 值 |");
            sb.AppendLine($"|------|-----|");
            sb.AppendLine($"| Mean IC | {icResult.MeanIC:F4} |");
            sb.AppendLine($"| IC Std | {icResult.StdIC:F4} |");
            sb.AppendLine($"| IC IR | {icResult.ICIR:F4} |");
            sb.AppendLine($"| t-stat | {icResult.TStatistic:F2} |");
            sb.AppendLine($"| IC>0% | {icResult.PositiveRatio:P1} |");
            sb.AppendLine($"| Half-Life | {icResult.HalfLife}期 |");
            sb.AppendLine($"| 结论 | **{icResult.Verdict}** |");
            sb.AppendLine();

            // Quantile Analysis
            var qtResult = evaluator.AnalyzeQuantiles(factorSnaps, 5);
            sb.AppendLine("### 分位数收益");
            sb.AppendLine($"| 分位 | 品种数 | 平均收益 | 年化收益 | Sharpe | 胜率 |");
            sb.AppendLine($"|------|--------|----------|----------|--------|------|");
            foreach (var q in qtResult.Quantiles)
                sb.AppendLine($"| Q{q.Quantile+1} | {q.Count} | {q.AvgReturn*10000:F1}bps | {q.AnnualReturn:P2} | {q.Sharpe:F2} | {q.WinRate:P1} |");
            sb.AppendLine($"| **多空** | - | - | **{qtResult.LongShortSpread:P2}** | - | - |");
            sb.AppendLine($"| 单调性 | {(qtResult.IsMonotonic ? "✅" : "⚠️")} | | | | |");
            sb.AppendLine();

            Console.WriteLine();
        }

        // Save report
        if (string.IsNullOrEmpty(outputPath))
            outputPath = Path.ChangeExtension(panelPath, ".ic-report.md");
        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"  Report: {outputPath}");

        return 0;
    }

    /// <summary>
    /// 从 Parquet 加载因子面板 → FactorSnapshot 列表。
    /// 使用 Python 互操作 (pythonnet) 或 duckdb.NET 直接读取。
    /// 简化实现: 调用 Python 脚本导出 CSV, 然后从 CSV 加载。
    /// </summary>
    private static async Task<List<FactorSnapshot>> LoadPanelFromParquet(string panelPath, List<string> filterFactors)
    {
        // 使用 Python 导出 CSV (最简单可靠的跨语言互操作)
        var csvPath = Path.ChangeExtension(panelPath, ".eval-temp.csv");
        var script = $@"
import pandas as pd
df = pd.read_parquet(r'{panelPath}')
df.to_csv(r'{csvPath}', index=False)
print(f'Exported {{len(df)}} rows')
";
        var psi = new System.Diagnostics.ProcessStartInfo("python3", $"-c \"{script.Replace("\"", "\\\"")}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return new();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0 || !File.Exists(csvPath))
                return new();
        }
        catch { return new(); }

        // 从 CSV 构建 FactorSnapshot
        var lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2) return new();

        var header = lines[0].Split(',');
        int idxDate = Array.IndexOf(header, "trading_day");
        int idxInst = Array.IndexOf(header, "instrument_id");

        if (idxDate < 0 || idxInst < 0) return new();

        // 发现所有因子列
        var factorIndices = new List<(string Name, int Index)>();
        for (int i = 0; i < header.Length; i++)
        {
            var name = header[i];
            if (name is "instrument_id" or "trading_day" or "bar_time" or "period" or "FwdRet_1d")
                continue;
            if (filterFactors.Count > 0 && !filterFactors.Contains(name)) continue;
            factorIndices.Add((name, i));
        }

        int idxFwd = Array.IndexOf(header, "FwdRet_1d");

        // Parse into per-day snapshots
        var dayData = new Dictionary<DateTime, (Dictionary<string, double> fv, Dictionary<string, double> fr)>();
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < Math.Max(idxDate, idxInst) + 1) continue;
            if (!DateTime.TryParse(parts[idxDate].Trim('"'), out var date)) continue;
            var inst = parts[idxInst].Trim('"');

            var fv = new Dictionary<string, double>();
            foreach (var (name, idx) in factorIndices)
            {
                if (idx < parts.Length && double.TryParse(parts[idx].Trim('"'),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val))
                    fv[name] = val;
            }

            double fwdRet = 0;
            if (idxFwd >= 0 && idxFwd < parts.Length)
                double.TryParse(parts[idxFwd].Trim('"'),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fwdRet);

            if (!dayData.TryGetValue(date, out var dd))
            {
                dd = (new Dictionary<string, double>(), new Dictionary<string, double>());
                dayData[date] = dd;
            }
            foreach (var (name, val) in fv)
            {
                dd.fv[name + "_" + inst] = val;
                dd.fr[name + "_" + inst] = fwdRet;
            }
        }

        // Cleanup temp file
        try { File.Delete(csvPath); } catch { }

        return dayData.Select(kv => new FactorSnapshot
        {
            Timestamp = kv.Key,
            FactorValues = kv.Value.fv,
            ForwardReturns = kv.Value.fr,
        }).OrderBy(s => s.Timestamp).ToList();
    }
}
