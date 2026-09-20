using System.Text;
using TradingStudio.Data.Factors;

namespace TradingStudio.Commands;

/// <summary>
/// Carry 反转因子复现评估 — 对照 scripts/factor_research/carry_csmom_factor.py 逐行移植。
///
/// 输出两件东西:
///   1. Rank IC（逐日截面 Spearman，common ≥ 10），前向 1/5/20 日，IS ≤ 2023-12-31 / OOS >。
///   2. 月度调仓 5 分组多空回测（horizon=21），多空 = Q1−Q5（做多低 Carry，反转方向）。
///
/// 验收闸门（docs/trading/因子研究/Carry反转因子深度分析.md）:
///   全样本多空 Q1−Q5 年化 +22.8% / Sharpe 1.44；OOS +23.2% / 1.60；IC 方向为负。
///   跑不出 → 回头查三口径，不进下一步。
///
/// 用法:
///   dotnet run -- carry-eval [--db C:\Works\Datas\bars_history.duckdb] [--is-end 2023-12-31] [--horizon 21] [--output carry.md]
/// </summary>
public class CarryEvalCommand
{
    private const double PeriodsPerYear = 252.0 / 21.0;   // 月度(21交易日)年化期数

    public static async Task<int> RunAsync(string[] args)
    {
        var dbPath = @"C:\Works\Datas\bars_history.duckdb";
        var isEnd = new DateTime(2023, 12, 31);
        var horizon = 21;
        var outputPath = "";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--db" or "-d" && i + 1 < args.Length) dbPath = args[++i];
            else if (args[i] is "--is-end" && i + 1 < args.Length) isEnd = DateTime.Parse(args[++i]);
            else if (args[i] is "--horizon" && i + 1 < args.Length) horizon = int.Parse(args[++i]);
            else if (args[i] is "--output" or "-o" && i + 1 < args.Length) outputPath = args[++i];
        }

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"DB not found: {dbPath}");
            return 1;
        }

        Console.WriteLine("═══ Carry 反转因子复现评估 ═══");
        Console.WriteLine($"  DB: {dbPath}");

        var panel = await CarryPanelBuilder.BuildAsync(dbPath);
        var carryPanel = panel.Carry;
        var closePanel = panel.ContinuousClose;

        // 回测交易日 = 连续合约面板里 ≥10 品种有数据的日期（与 Python close_panel 过滤一致）
        var dates = closePanel.Where(kv => kv.Value.Count >= 10)
            .Select(kv => kv.Key).OrderBy(d => d).ToList();

        Console.WriteLine($"  Carry 面板: {carryPanel.Count} 天 | 连续面板: {dates.Count} 天 | 品种: {panel.Products.Count}");
        if (dates.Count < horizon + 20)
        {
            Console.Error.WriteLine("数据不足: 交易日 < horizon+20");
            return 1;
        }
        if (dates.Count > 0)
            Console.WriteLine($"  区间: {dates[0]:yyyy-MM-dd} ~ {dates[^1]:yyyy-MM-dd}");

        var sb = new StringBuilder();
        sb.AppendLine($"# Carry 反转因子复现报告");
        sb.AppendLine($"> DB: {Path.GetFileName(dbPath)}  |  生成: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"> IS ≤ {isEnd:yyyy-MM-dd}  |  OOS > {isEnd:yyyy-MM-dd}  |  调仓 horizon={horizon}");
        sb.AppendLine();

        // ── 1. Rank IC ──
        Console.WriteLine("\n── Rank IC (逐日截面 Spearman, common ≥ 10) ──");
        sb.AppendLine("## Rank IC");
        sb.AppendLine();
        sb.AppendLine($"| 前向 | IS IC | IS IR | IS IC&gt;0% | OOS IC | OOS IR | OOS IC&gt;0% | n(IS/OOS) |");
        sb.AppendLine($"|------|-------|-------|-----------|--------|--------|-----------|-----------|");
        foreach (var h in new[] { 1, 5, 20 })
        {
            var ics = ComputeIcSeries(carryPanel, closePanel, dates, h);
            var isIc = IcStats(ics.Where(x => x.T <= isEnd).Select(x => x.Ic).ToList());
            var oosIc = IcStats(ics.Where(x => x.T > isEnd).Select(x => x.Ic).ToList());
            Console.WriteLine($"  {h,2}d | IS IC {Fmt(isIc.Mean, "+0.000")} IR {Fmt(isIc.Ir, "+0.00")} >0 {isIc.PosRatio,7:P0} " +
                              $"| OOS IC {Fmt(oosIc.Mean, "+0.000")} IR {Fmt(oosIc.Ir, "+0.00")} >0 {oosIc.PosRatio,7:P0} | n={isIc.N}/{oosIc.N}");
            sb.AppendLine($"| {h}d | {isIc.Mean:+0.000} | {isIc.Ir:+0.00} | {isIc.PosRatio:P0} | {oosIc.Mean:+0.000} | {oosIc.Ir:+0.00} | {oosIc.PosRatio:P0} | {isIc.N}/{oosIc.N} |");
        }
        sb.AppendLine();

        // ── 2. 月度 5 分组多空 ──
        Console.WriteLine("\n── 月度调仓 5 分组多空 (Q1−Q5, 反转) ──");
        sb.AppendLine("## 月度 5 分组多空");
        sb.AppendLine();
        var (ls, quintileRets) = QuantileBacktest(carryPanel, closePanel, dates, horizon);

        if (ls.Count == 0)
        {
            Console.WriteLine("  数据不足");
            sb.AppendLine("数据不足。");
        }
        else
        {
            sb.AppendLine("### 分位年化");
            sb.AppendLine();
            sb.AppendLine($"| 分位 | 年化收益 | Sharpe | n |");
            sb.AppendLine($"|------|----------|--------|---|");
            for (int q = 1; q <= 5; q++)
            {
                var (ann, sharpe, n) = Annualize(quintileRets[q].Select(x => x.Ret).ToList());
                Console.WriteLine($"  Q{q}  年化 {ann,9:P1}  Sharpe {sharpe,6:F2}  n={n}");
                sb.AppendLine($"| Q{q} | {ann:P1} | {sharpe:F2} | {n} |");
            }

            sb.AppendLine();
            sb.AppendLine("### 多空 (Q1−Q5)");
            sb.AppendLine();
            sb.AppendLine($"| 周期 | 年化 | Sharpe | n |");
            sb.AppendLine($"|------|------|--------|---|");
            foreach (var (label, slice) in new[] { ("IS", ls.Where(x => x.T <= isEnd).ToList()), ("OOS", ls.Where(x => x.T > isEnd).ToList()) })
            {
                var (ann, sharpe, n) = Annualize(slice.Select(x => x.Ret).ToList());
                Console.WriteLine($"  多空 {label}: 年化 {ann,8:P1}  Sharpe {sharpe,6:F2}  n={n}");
                sb.AppendLine($"| {label} | {ann:P1} | {sharpe:F2} | {n} |");
            }
            var (annAll, sharpeAll, nAll) = Annualize(ls.Select(x => x.Ret).ToList());
            Console.WriteLine($"  多空 全样本: 年化 {annAll,8:P1}  Sharpe {sharpeAll,6:F2}  n={nAll}");
            sb.AppendLine($"| 全样本 | {annAll:P1} | {sharpeAll:F2} | {nAll} |");

            sb.AppendLine();
            sb.AppendLine("> 验收: 全样本 Sharpe≈1.44、OOS≈1.60。跑不出回头查三口径。");
        }
        sb.AppendLine();

        // ── 3. 保存 ──
        if (string.IsNullOrEmpty(outputPath))
            outputPath = Path.Combine(AppContext.BaseDirectory, "carry-eval-report.md");
        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"\n  Report: {outputPath}");

        return 0;
    }

    /// <summary>逐日截面 Spearman IC。前向 h 日收益用连续合约 close[t+h]/close[t]-1。</summary>
    private static List<(DateTime T, double Ic)> ComputeIcSeries(
        Dictionary<DateTime, Dictionary<string, double>> carryPanel,
        Dictionary<DateTime, Dictionary<string, double>> closePanel,
        List<DateTime> dates, int h)
    {
        var result = new List<(DateTime, double)>();
        for (int i = 0; i < dates.Count - h; i++)
        {
            var t = dates[i];
            if (!carryPanel.TryGetValue(t, out var carryByProd)) continue;
            if (!closePanel.TryGetValue(t, out var closeT) || !closePanel.TryGetValue(dates[i + h], out var closeTh)) continue;

            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var (prod, carry) in carryByProd)
            {
                if (closeT.TryGetValue(prod, out var c0) && closeTh.TryGetValue(prod, out var c1) && c0 > 0 && c1 > 0)
                {
                    xs.Add(carry);
                    ys.Add(c1 / c0 - 1);
                }
            }
            if (xs.Count < 10) continue;
            var ic = Spearman(xs, ys);
            if (!double.IsNaN(ic)) result.Add((t, ic));
        }
        return result;
    }

    /// <summary>月度调仓 5 分组多空，多空 = Q1 − Q5（做多低 Carry）。</summary>
    private static (List<(DateTime T, double Ret)> Ls, Dictionary<int, List<(DateTime T, double Ret)>> Quintiles)
        QuantileBacktest(
            Dictionary<DateTime, Dictionary<string, double>> carryPanel,
            Dictionary<DateTime, Dictionary<string, double>> closePanel,
            List<DateTime> dates, int horizon)
    {
        var ls = new List<(DateTime, double)>();
        var quintiles = new Dictionary<int, List<(DateTime, double)>>();
        for (int q = 1; q <= 5; q++) quintiles[q] = new();

        for (int i = 0; i < dates.Count - horizon; i += horizon)
        {
            var t = dates[i];
            var tNext = dates[i + horizon];
            if (!carryPanel.TryGetValue(t, out var carryByProd)) continue;
            if (!closePanel.TryGetValue(t, out var closeT) || !closePanel.TryGetValue(tNext, out var closeNext)) continue;

            var items = new List<(string Prod, double Carry, double Fwd)>();
            foreach (var (prod, carry) in carryByProd)
            {
                if (closeT.TryGetValue(prod, out var c0) && closeNext.TryGetValue(prod, out var c1) && c0 > 0 && c1 > 0)
                    items.Add((prod, carry, c1 / c0 - 1));
            }
            if (items.Count < 10) continue;

            // 按 carry 升序，qcut 成 5 等份 → Q1=最低 carry（深升水）, Q5=最高 carry（深贴水）
            items.Sort((a, b) => a.Carry.CompareTo(b.Carry));
            var n = items.Count;
            var sum = new double[6];
            var cnt = new int[6];
            for (int j = 0; j < n; j++)
            {
                int q = Math.Min(4, j * 5 / n) + 1;
                sum[q] += items[j].Fwd;
                cnt[q]++;
            }

            var mean = new double[6];
            for (int q = 1; q <= 5; q++)
            {
                if (cnt[q] == 0) continue;
                mean[q] = sum[q] / cnt[q];
                quintiles[q].Add((t, mean[q]));
            }
            ls.Add((t, mean[1] - mean[5]));
        }
        return (ls, quintiles);
    }

    private static (double Ann, double Sharpe, int N) Annualize(List<double> rets)
    {
        if (rets.Count == 0) return (0, double.NaN, 0);
        double total = rets.Aggregate(1.0, (acc, r) => acc * (1 + r)) - 1.0;
        int n = rets.Count;
        double ann = Math.Pow(1 + total, PeriodsPerYear / n) - 1.0;
        double mean = rets.Average();
        double var = rets.Sum(r => (r - mean) * (r - mean)) / (n - 1);
        double vol = Math.Sqrt(var) * Math.Sqrt(PeriodsPerYear);
        double sharpe = vol > 0 ? ann / vol : double.NaN;
        return (ann, sharpe, n);
    }

    private static (double Mean, double Std, double Ir, double PosRatio, int N) IcStats(List<double> ics)
    {
        if (ics.Count == 0) return (double.NaN, double.NaN, double.NaN, double.NaN, 0);
        double mean = ics.Average();
        double var = ics.Sum(x => (x - mean) * (x - mean)) / (ics.Count - 1);
        double std = Math.Sqrt(var);
        double ir = std > 0 ? mean / std : double.NaN;
        double pos = (double)ics.Count(x => x > 0) / ics.Count;
        return (mean, std, ir, pos, ics.Count);
    }

    private static double Spearman(List<double> x, List<double> y)
    {
        var rx = Rank(x);
        var ry = Rank(y);
        return Pearson(rx, ry);
    }

    /// <summary>升序平均秩（处理并列）。</summary>
    private static double[] Rank(List<double> v)
    {
        var order = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToArray();
        var ranks = new double[v.Count];
        int k = 0;
        while (k < order.Length)
        {
            int j = k;
            while (j < order.Length && v[order[j]] == v[order[k]]) j++;
            double avg = (k + j - 1) / 2.0 + 1.0;
            for (int m = k; m < j; m++) ranks[order[m]] = avg;
            k = j;
        }
        return ranks;
    }

    private static double Pearson(double[] a, double[] b)
    {
        if (a.Length == 0) return double.NaN;
        double ma = a.Average(), mb = b.Average();
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double xa = a[i] - ma, xb = b[i] - mb;
            num += xa * xb;
            da += xa * xa;
            db += xb * xb;
        }
        double denom = Math.Sqrt(da * db);
        return denom == 0 ? double.NaN : num / denom;
    }

    private static string Fmt(double v, string fmt) => double.IsNaN(v) ? "  ---" : v.ToString(fmt);
}
