namespace TradingStudio.Core.Analysis;

/// <summary>
/// 因子评估器 — IC 分析 + 分位数收益分析。
///
/// 这是从 Chan 到 Gray 的关键升级：
///   Chan: 看 Sharpe → "这个策略赚钱吗？"
///   Gray: 看 IC   → "这个因子有预测能力吗？预测力稳定吗？会衰减吗？"
///
/// IC (Information Coefficient):
///   Rank IC = corr(factor_percentile(t), forward_return(t+1))
///   衡量因子值与未来收益的排名相关性。
///
/// 分位数收益:
///   按因子值将品种分成 5 组，计算每组的平均未来收益。
///   Q5 - Q1 = 因子多空收益差。
///   单调递增的收益曲线 = 优质因子。
/// </summary>
public class FactorEvaluator
{
    private readonly EvalConfig _config;

    public FactorEvaluator(EvalConfig? config = null)
    {
        _config = config ?? new EvalConfig();
    }

    // ═══════════════════════════════════════════
    // IC 分析
    // ═══════════════════════════════════════════

    /// <summary>
    /// 计算 Rank IC 序列。
    /// 输入: 每期的因子百分位 + 下一期的实际收益率
    /// 输出: 每期的 Rank IC (Spearman 相关系数)
    /// </summary>
    public IcAnalysisResult AnalyzeIC(
        IReadOnlyList<FactorSnapshot> factorSnapshots)
    {
        if (factorSnapshots.Count < 20)
            return new IcAnalysisResult { IsInsufficientData = true };

        // 按时间排序
        var sorted = factorSnapshots.OrderBy(s => s.Timestamp).ToList();

        var icSeries = new List<double>();
        var timestamps = new List<DateTime>();

        for (int t = 0; t < sorted.Count - 1; t++)
        {
            var current = sorted[t];
            var next = sorted[t + 1];

            // 本期因子百分位 vs 下一期收益 → Rank IC
            var ic = ComputeRankIC(current.FactorValues, next.ForwardReturns);
            icSeries.Add(ic);
            timestamps.Add(current.Timestamp);
        }

        var meanIC = icSeries.Average();
        var stdIC = StdDev(icSeries);
        var icIR = stdIC > 0 ? meanIC / stdIC : 0;

        // IC 胜率（IC > 0 的比例）
        var positiveRatio = icSeries.Count(ic => ic > 0) / (double)icSeries.Count;

        // IC t 统计量 (mean / (std/sqrt(n)))
        var tStat = stdIC > 0 ? meanIC / (stdIC / Math.Sqrt(icSeries.Count)) : 0;

        // IC 衰减分析（Lag 1, 2, 3, 5, 10, 20 期的 IC）
        var decay = ComputeICDecay(sorted);

        // IC 累积（累积 IC = Σ IC_t → 应该持续增长）
        var cumulativeIC = new List<double>();
        var cumSum = 0.0;
        foreach (var ic in icSeries)
        {
            cumSum += ic;
            cumulativeIC.Add(cumSum);
        }

        return new IcAnalysisResult
        {
            MeanIC = meanIC,
            StdIC = stdIC,
            ICIR = icIR,
            PositiveRatio = positiveRatio,
            TStatistic = tStat,
            SampleSize = icSeries.Count,
            ICSeries = icSeries,
            ICSeriesTimestamps = timestamps,
            CumulativeIC = cumulativeIC,
            ICDecay = decay,
            Verdict = icIR switch
            {
                >= 0.50 => IcVerdict.Excellent,  // Gray: IC IR > 0.5 是优秀因子
                >= 0.30 => IcVerdict.Good,       // Gray: IC IR > 0.3 是可接受因子
                >= 0.10 => IcVerdict.Weak,
                _ => IcVerdict.Insufficient,
            },
        };
    }

    /// <summary>
    /// 计算单期 Rank IC = corr(factor_values, forward_returns) using Spearman rank
    /// </summary>
    private static double ComputeRankIC(
        Dictionary<string, double> factorValues,
        Dictionary<string, double> forwardReturns)
    {
        // 取交集
        var common = factorValues.Keys.Intersect(forwardReturns.Keys).ToList();
        if (common.Count < 5) return 0;

        var fv = common.Select(k => factorValues[k]).ToList();
        var fr = common.Select(k => forwardReturns[k]).ToList();

        return SpearmanRank(fv, fr);
    }

    /// <summary>Spearman 秩相关系数</summary>
    private static double SpearmanRank(List<double> x, List<double> y)
    {
        var n = x.Count;

        // 排名（等值取平均排名）
        var rankX = Rank(x);
        var rankY = Rank(y);

        // Pearson on ranks = Spearman
        var meanRx = rankX.Average();
        var meanRy = rankY.Average();
        var cov = 0.0;
        var varX = 0.0;
        var varY = 0.0;

        for (int i = 0; i < n; i++)
        {
            var dx = rankX[i] - meanRx;
            var dy = rankY[i] - meanRy;
            cov += dx * dy;
            varX += dx * dx;
            varY += dy * dy;
        }

        return varX > 0 && varY > 0 ? cov / Math.Sqrt(varX * varY) : 0;
    }

    private static List<double> Rank(List<double> values)
    {
        var indexed = values.Select((v, i) => (Value: v, Index: i))
            .OrderBy(x => x.Value)
            .ToList();

        var ranks = new double[values.Count];
        for (int i = 0; i < indexed.Count;)
        {
            int j = i;
            while (j < indexed.Count && indexed[j].Value == indexed[i].Value) j++;
            var avgRank = (i + j - 1) / 2.0 + 1; // 1-based rank
            for (int k = i; k < j; k++)
                ranks[indexed[k].Index] = avgRank;
            i = j;
        }
        return ranks.ToList();
    }

    /// <summary>IC 衰减: 因子预测能力随 lag 增加如何衰减</summary>
    private Dictionary<int, double> ComputeICDecay(List<FactorSnapshot> sorted)
    {
        var lags = new[] { 1, 2, 3, 5, 10, 20 };
        var decay = new Dictionary<int, double>();

        foreach (var lag in lags)
        {
            var ics = new List<double>();
            for (int t = 0; t < sorted.Count - lag; t++)
            {
                var ic = ComputeRankIC(sorted[t].FactorValues, sorted[t + lag].ForwardReturns);
                ics.Add(ic);
            }
            decay[lag] = ics.Count > 0 ? ics.Average() : 0;
        }

        return decay;
    }

    // ═══════════════════════════════════════════
    // 分位数收益分析
    // ═══════════════════════════════════════════

    /// <summary>
    /// 分位数收益分析 — 按因子值分组，看各组平均收益是否单调递增。
    /// </summary>
    public QuantileAnalysisResult AnalyzeQuantiles(
        IReadOnlyList<FactorSnapshot> factorSnapshots,
        int numQuantiles = 5)
    {
        if (factorSnapshots.Count < 20)
            return new QuantileAnalysisResult { IsInsufficientData = true };

        // 合并所有时期的 factor_value → forward_return 对
        var allPairs = new List<(double FactorValue, double ForwardReturn)>();
        foreach (var snapshot in factorSnapshots)
        {
            var common = snapshot.FactorValues.Keys
                .Intersect(snapshot.ForwardReturns.Keys).ToList();
            foreach (var key in common)
                allPairs.Add((snapshot.FactorValues[key], snapshot.ForwardReturns[key]));
        }

        if (allPairs.Count < numQuantiles * 5)
            return new QuantileAnalysisResult { IsInsufficientData = true };

        // 按因子值排序并分组
        var sortedPairs = allPairs.OrderBy(p => p.FactorValue).ToList();
        var groupSize = sortedPairs.Count / numQuantiles;

        var quantiles = new List<QuantileStat>();
        for (int q = 0; q < numQuantiles; q++)
        {
            var start = q * groupSize;
            var end = q == numQuantiles - 1 ? sortedPairs.Count : start + groupSize;
            var group = sortedPairs.GetRange(start, end - start);
            var returns = group.Select(p => p.ForwardReturn).ToList();
            var avgReturn = returns.Average();
            var stdReturn = StdDev(returns);
            var winRate = returns.Count(r => r > 0) / (double)returns.Count;
            // 年化（假设日频）
            var annualReturn = avgReturn * 252;

            quantiles.Add(new QuantileStat
            {
                Quantile = q + 1,
                Label = q switch
                {
                    0 => $"Q{q+1} (最弱 {numQuantiles*20}%)",
                    _ when q == numQuantiles - 1 => $"Q{q+1} (最强 {numQuantiles*20}%)",
                    _ => $"Q{q+1}",
                },
                Count = group.Count,
                AvgReturn = avgReturn,
                AnnualReturn = annualReturn,
                StdReturn = stdReturn,
                Sharpe = stdReturn > 0 ? annualReturn / (stdReturn * Math.Sqrt(252)) : 0,
                WinRate = winRate,
            });
        }

        // 多空收益差
        var longShort = quantiles.Last().AvgReturn - quantiles.First().AvgReturn;
        var longShortAnnual = quantiles.Last().AnnualReturn - quantiles.First().AnnualReturn;

        // 单调性检测
        var monotonic = true;
        for (int i = 1; i < quantiles.Count; i++)
        {
            if (quantiles[i].AvgReturn < quantiles[i - 1].AvgReturn)
                monotonic = false;
        }

        return new QuantileAnalysisResult
        {
            Quantiles = quantiles,
            LongShortSpread = longShort,
            LongShortAnnual = longShortAnnual,
            IsMonotonic = monotonic,
            TotalPairs = allPairs.Count,
            Verdict = (longShortAnnual, monotonic) switch
            {
                ( > 0.05, true) => "优质因子 — 分位数收益单调递增，多空收益显著",
                ( > 0.02, true) => "可用因子 — 有区分力但收益差不显著",
                ( > 0, false) => "因子有效但非单调 — 检查中间分位数",
                _ => "因子无效 — 无区分力或方向错误",
            },
        };
    }

    private static double StdDev(List<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return Math.Sqrt(values.Average(v => Math.Pow(v - mean, 2)));
    }
}

/// <summary>单期的因子快照（用于 IC 分析）</summary>
public class FactorSnapshot
{
    public DateTime Timestamp { get; init; }

    /// <summary>品种 → 因子值（百分位或 z-score）</summary>
    public Dictionary<string, double> FactorValues { get; init; } = new();

    /// <summary>品种 → 下一期实际收益率</summary>
    public Dictionary<string, double> ForwardReturns { get; init; } = new();
}

/// <summary>评估配置</summary>
public class EvalConfig
{
    /// <summary>最小样本量</summary>
    public int MinSamples { get; init; } = 30;

    /// <summary>IC 衰减测试的最大 lag</summary>
    public int MaxDecayLag { get; init; } = 20;
}

/// <summary>IC 分析结果</summary>
public class IcAnalysisResult
{
    public bool IsInsufficientData { get; init; }

    /// <summary>平均 Rank IC — 正值 = 正向预测</summary>
    public double MeanIC { get; init; }

    /// <summary>IC 标准差 — 越小越稳定</summary>
    public double StdIC { get; init; }

    /// <summary>IC Information Ratio = MeanIC / StdIC。Gray: >0.3 可用，>0.5 优秀</summary>
    public double ICIR { get; init; }

    /// <summary>IC > 0 的比例</summary>
    public double PositiveRatio { get; init; }

    /// <summary>IC 均值的 t 统计量</summary>
    public double TStatistic { get; init; }

    public int SampleSize { get; init; }

    /// <summary>IC 时间序列（每期一个 IC 值）</summary>
    public List<double> ICSeries { get; init; } = [];

    public List<DateTime> ICSeriesTimestamps { get; init; } = [];

    /// <summary>累积 IC 曲线（应该持续增长）</summary>
    public List<double> CumulativeIC { get; init; } = [];

    /// <summary>IC 衰减: lag → mean IC at that lag</summary>
    public Dictionary<int, double> ICDecay { get; init; } = new();

    public IcVerdict Verdict { get; init; }

    /// <summary>IC 半衰期: 衰减到 MeanIC/2 的 lag。越长越好。</summary>
    public int HalfLife
    {
        get
        {
            var half = MeanIC / 2;
            foreach (var (lag, ic) in ICDecay.OrderBy(kv => kv.Key))
                if (ic < half) return lag;
            return ICDecay.Keys.DefaultIfEmpty(0).Max();
        }
    }
}

public enum IcVerdict
{
    Excellent,   // IC IR >= 0.5
    Good,        // IC IR >= 0.3
    Weak,        // IC IR >= 0.1
    Insufficient, // IC IR < 0.1
}

/// <summary>分位数分析结果</summary>
public class QuantileAnalysisResult
{
    public bool IsInsufficientData { get; init; }
    public List<QuantileStat> Quantiles { get; init; } = [];

    /// <summary>多空收益差（Q5 - Q1 单期）</summary>
    public double LongShortSpread { get; init; }

    /// <summary>多空年化收益</summary>
    public double LongShortAnnual { get; init; }

    /// <summary>收益是否随因子递增</summary>
    public bool IsMonotonic { get; init; }

    public int TotalPairs { get; init; }
    public string Verdict { get; init; } = "";
}

public class QuantileStat
{
    public int Quantile { get; init; }
    public string Label { get; init; } = "";
    public int Count { get; init; }
    public double AvgReturn { get; init; }
    public double AnnualReturn { get; init; }
    public double StdReturn { get; init; }
    public double Sharpe { get; init; }
    public double WinRate { get; init; }
}
