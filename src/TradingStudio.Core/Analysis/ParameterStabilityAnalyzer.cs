namespace TradingStudio.Core.Analysis;

/// <summary>
/// 参数稳定性分析器 — Ernie Chan《Algorithmic Trading》Ch3 的核心概念。
///
/// 核心问题：如果参数稍微变一下，策略还能赚钱吗？
///
/// Chan 的核心观点：
///   回测中总能找到一组参数让 Sharpe 很好看。
///   但真正的问题是——这组参数周围的 "地形" 是平台还是尖峰？
///   平台（参数稍有变化 Sharpe 几乎不变）→ 稳健
///   尖峰（只有这组参数好用，偏一点就崩）→ 过拟合
///
/// 使用方法：
///   1. 用网格搜索跑出参数×Sharpe 矩阵
///   2. 把结果传入 Analyze() 得到稳定性报告
/// </summary>
public class ParameterStabilityAnalyzer
{
    private readonly StabilityConfig _config;

    public ParameterStabilityAnalyzer(StabilityConfig? config = null)
    {
        _config = config ?? new StabilityConfig();
    }

    /// <summary>
    /// 分析单个参数在网格扫描后的稳定性。
    /// 传入按参数值排序的性能列表，返回稳定性评估。
    /// </summary>
    public ParameterStabilityReport AnalyzeSingleParameter(
        string parameterName,
        List<(double ParamValue, double Sharpe)> scanResults)
    {
        if (scanResults.Count < 3)
            return new ParameterStabilityReport { ParameterName = parameterName, IsInsufficientData = true };

        var sorted = scanResults.OrderBy(x => x.ParamValue).ToList();
        var sharpes = sorted.Select(x => x.Sharpe).ToList();
        var params_ = sorted.Select(x => x.ParamValue).ToList();

        var maxSharpe = sharpes.Max();
        var maxIndex = sharpes.IndexOf(maxSharpe);
        var optimalParam = params_[maxIndex];

        // ── 1. 计算相邻点波动（Sharpe 随参数变化的一阶差分）──
        var diffs = new List<double>();
        for (int i = 1; i < sharpes.Count; i++)
            diffs.Add(Math.Abs(sharpes[i] - sharpes[i - 1]));
        var avgDiff = diffs.Count > 0 ? diffs.Average() : 0;

        // ── 2. 最优参数附近的局部稳定性（±20% 范围内 Sharpe 的标准差）──
        var localSharpes = new List<double>();
        var localRange = optimalParam * _config.LocalRangeRatio;
        foreach (var (pv, s) in sorted)
        {
            if (Math.Abs(pv - optimalParam) <= localRange)
                localSharpes.Add(s);
        }
        var localStd = localSharpes.Count >= 3 ? StdDev(localSharpes) : double.NaN;

        // ── 3. 全局稳定性 — Sharpe 变异系数 ──
        var meanSharpe = sharpes.Average();
        var globalStd = StdDev(sharpes);
        var globalCV = meanSharpe != 0 ? globalStd / Math.Abs(meanSharpe) : double.PositiveInfinity;

        // ── 4. 稳定性评分 ──
        // = 最优Sharpe × (1 - CV_local) / (1 + CV_global)
        // 高Sharpe + 低局部波动 + 低全局波动 = 高分
        var localCV = !double.IsNaN(localStd) && maxSharpe != 0
            ? Math.Min(localStd / Math.Abs(maxSharpe), 1.0) : 1.0;
        var stabilityScore = maxSharpe * (1 - localCV) / (1 + globalCV);

        // ── 5. 参数平原检测 ──
        var plateauRegion = FindPlateau(sorted, maxSharpe);

        return new ParameterStabilityReport
        {
            ParameterName = parameterName,
            OptimalValue = optimalParam,
            MaxSharpe = maxSharpe,
            MeanSharpe = meanSharpe,
            GlobalStd = globalStd,
            GlobalCV = globalCV,
            LocalStd = localStd,
            LocalCV = localCV,
            AvgAdjacentDiff = avgDiff,
            StabilityScore = stabilityScore,
            PlateauStart = plateauRegion.Start,
            PlateauEnd = plateauRegion.End,
            PlateauWidth = plateauRegion.Width,
            PlateauWidthRatio = plateauRegion.WidthRatio,
            ScanPointsCount = scanResults.Count,
            ScanValueRange = params_.Max() - params_.Min(),

            Verdict = stabilityScore switch
            {
                >= 0.6 => StabilityVerdict.Robust,
                >= 0.3 => StabilityVerdict.Acceptable,
                >= 0.1 => StabilityVerdict.Fragile,
                _ => StabilityVerdict.Overfit,
            },

            Warning = DetermineWarning(globalCV, localCV, plateauRegion.WidthRatio),
        };
    }

    /// <summary>
    /// 多参数交互分析 — 检测参数之间的依赖关系。
    /// 如果两个参数的最优值高度耦合（改变一个必须同时改另一个），说明过拟合风险高。
    /// </summary>
    public MultiParameterReport AnalyzeParameterInteraction(
        List<(double Param1, double Param2, double Sharpe)> gridResults)
    {
        if (gridResults.Count < 9)
            return new MultiParameterReport { IsInsufficientData = true };

        var sharpes = gridResults.Select(r => r.Sharpe).ToList();
        var maxSharpe = sharpes.Max();

        // 找到 top 10% 的参数组合
        var threshold = sharpes.OrderByDescending(s => s)
            .Skip((int)(sharpes.Count * 0.1))
            .FirstOrDefault(maxSharpe * 0.9);
        var topCombos = gridResults.Where(r => r.Sharpe >= threshold).ToList();

        if (topCombos.Count < 3)
            return new MultiParameterReport { IsInsufficientData = true };

        // 计算 top 组合中两个参数的标准差 → 越小说明参数越耦合
        var p1Std = StdDev(topCombos.Select(r => r.Param1).ToList());
        var p2Std = StdDev(topCombos.Select(r => r.Param2).ToList());

        // 计算 top 组合在两个参数维度上的覆盖宽度
        var p1Range = topCombos.Max(r => r.Param1) - topCombos.Min(r => r.Param1);
        var p2Range = topCombos.Max(r => r.Param2) - topCombos.Min(r => r.Param2);
        var p1FullRange = gridResults.Max(r => r.Param1) - gridResults.Min(r => r.Param1);
        var p2FullRange = gridResults.Max(r => r.Param2) - gridResults.Min(r => r.Param2);

        // 覆盖率 = Top 组合覆盖了参数空间的多少
        var coverageRatio = (p1Range / p1FullRange) * (p2Range / p2FullRange);

        return new MultiParameterReport
        {
            TopCombinationsCount = topCombos.Count,
            TotalCombinationsCount = gridResults.Count,
            TopSharpeThreshold = threshold,
            MaxSharpe = maxSharpe,
            Param1Std = p1Std,
            Param2Std = p2Std,
            CoverageRatio = coverageRatio,
            Verdict = coverageRatio switch
            {
                >= 0.15 => "参数独立性强 — 多个组合都能达到相近效果 ✓",
                >= 0.05 => "参数有一定耦合 — 注意防止过拟合 ⚠️",
                _ => "参数高度耦合 — 仅极窄范围有效，强烈提示过拟合 ❌"
            },
        };
    }

    /// <summary>
    /// 找到参数高原区域：Sharpe 在 maxSharpe * (1 - PlateauTolerance) 范围内的参数区间。
    /// 高原越宽（相对参数总范围的百分比越大）→ 策略越稳健。
    /// </summary>
    private (double Start, double End, double Width, double WidthRatio) FindPlateau(
        List<(double ParamValue, double Sharpe)> sorted,
        double maxSharpe)
    {
        var threshold = maxSharpe * (1 - _config.PlateauTolerance);
        var plateauPoints = sorted.Where(x => x.Sharpe >= threshold).ToList();

        if (plateauPoints.Count < 2)
            return (sorted[0].ParamValue, sorted[0].ParamValue, 0, 0);

        var start = plateauPoints.First().ParamValue;
        var end = plateauPoints.Last().ParamValue;
        var width = end - start;
        var totalRange = sorted.Last().ParamValue - sorted.First().ParamValue;
        var ratio = totalRange > 0 ? width / totalRange : 0;

        return (start, end, width, ratio);
    }

    private static string DetermineWarning(double globalCV, double localCV, double plateauRatio)
    {
        var warnings = new List<string>();

        if (globalCV > 0.5)
            warnings.Add("全局 Sharpe 波动大（CV=" + globalCV.ToString("F2") + "），策略表现对参数敏感");
        if (!double.IsNaN(localCV) && localCV > 0.2)
            warnings.Add("最优参数附近 Sharpe 不稳定（局部CV=" + localCV.ToString("F2") + "），尖峰特征");
        if (plateauRatio < 0.1)
            warnings.Add("参数高原极窄（" + (plateauRatio * 100).ToString("F1") + "%），仅极窄参数范围有效");

        return warnings.Count > 0 ? string.Join("；", warnings) : "参数稳定性良好";
    }

    private static double StdDev(List<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return Math.Sqrt(values.Average(v => Math.Pow(v - mean, 2)));
    }
}

/// <summary>参数稳定性分析配置</summary>
public class StabilityConfig
{
    /// <summary>
    /// 局部范围比例 — 最优参数的 ±N% 范围视为 "局部"。
    /// 默认 0.20 即 ±20%。
    /// </summary>
    public double LocalRangeRatio { get; init; } = 0.20;

    /// <summary>
    /// 高原容忍度 — Sharpe 在 maxSharpe × (1 - tolerance) 以上的区域视为 "高原"。
    /// 默认 0.10 即 maxSharpe 的 90%。
    /// </summary>
    public double PlateauTolerance { get; init; } = 0.10;
}

/// <summary>单参数稳定性报告</summary>
public class ParameterStabilityReport
{
    public bool IsInsufficientData { get; init; }
    public string ParameterName { get; init; } = "";

    /// <summary>最优参数值</summary>
    public double OptimalValue { get; init; }

    /// <summary>最优参数对应的 Sharpe</summary>
    public double MaxSharpe { get; init; }

    /// <summary>全部扫描点的 Sharpe 均值</summary>
    public double MeanSharpe { get; init; }

    /// <summary>全局 Sharpe 标准差 — 整个扫描范围内</summary>
    public double GlobalStd { get; init; }

    /// <summary>全局变异系数 (CV = std/|mean|) — 越小越好</summary>
    public double GlobalCV { get; init; }

    /// <summary>局部 Sharpe 标准差 — 最优参数 ±20% 范围内</summary>
    public double LocalStd { get; init; }

    /// <summary>局部变异系数 — 越小说明最优参数附近是 "高原"</summary>
    public double LocalCV { get; init; }

    /// <summary>相邻参数点之间的平均 Sharpe 变化（一阶差分均值）</summary>
    public double AvgAdjacentDiff { get; init; }

    /// <summary>综合稳定性评分 — 越高越好</summary>
    public double StabilityScore { get; init; }

    /// <summary>参数高原起点</summary>
    public double PlateauStart { get; init; }

    /// <summary>参数高原终点</summary>
    public double PlateauEnd { get; init; }

    /// <summary>参数高原宽度（绝对值）</summary>
    public double PlateauWidth { get; init; }

    /// <summary>参数高原宽度比例（占总范围的 %）</summary>
    public double PlateauWidthRatio { get; init; }

    public int ScanPointsCount { get; init; }
    public double ScanValueRange { get; init; }

    public StabilityVerdict Verdict { get; init; }
    public string Warning { get; init; } = "";

    /// <summary>
    /// 生成 Markdown 格式的稳定性报告。
    /// 可直接写入 Obsidian 策略文档的参数稳健性一节。
    /// </summary>
    public string ToMarkdown()
    {
        if (IsInsufficientData)
            return $"**{ParameterName}**: 数据不足，无法分析稳定性。";

        var verdictIcon = Verdict switch
        {
            StabilityVerdict.Robust => "✅",
            StabilityVerdict.Acceptable => "⚠️",
            StabilityVerdict.Fragile => "🔴",
            StabilityVerdict.Overfit => "❌",
            _ => "❓"
        };

        return $"""
            ### {ParameterName} 稳定性 {verdictIcon}

            | 指标 | 值 | 说明 |
            |------|:--:|------|
            | 最优值 | {OptimalValue:F2} | 最大 Sharpe = {MaxSharpe:F3} |
            | 全局 CV | {GlobalCV:F3} | {(GlobalCV < 0.3 ? "✓ 稳定" : "⚠️ 波动大")} |
            | 局部 CV | {LocalCV:F3} | {(!double.IsNaN(LocalCV) ? (LocalCV < 0.15 ? "✓ 平台" : "⚠️ 尖峰") : "N/A")} |
            | 高原宽度 | {PlateauWidthRatio:P1} | {PlateauStart:F2} ~ {PlateauEnd:F2} |
            | 评分 | {StabilityScore:F3} | {Verdict} |

            **结论**: {Warning}
            """;
    }
}

/// <summary>多参数交互报告</summary>
public class MultiParameterReport
{
    public bool IsInsufficientData { get; init; }
    public int TopCombinationsCount { get; init; }
    public int TotalCombinationsCount { get; init; }
    public double TopSharpeThreshold { get; init; }
    public double MaxSharpe { get; init; }
    public double Param1Std { get; init; }
    public double Param2Std { get; init; }
    public double CoverageRatio { get; init; }
    public string Verdict { get; init; } = "";
}

/// <summary>参数稳定性结论</summary>
public enum StabilityVerdict
{
    Robust,
    Acceptable,
    Fragile,
    Overfit,
}
