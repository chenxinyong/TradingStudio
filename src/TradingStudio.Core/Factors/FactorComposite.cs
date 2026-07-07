namespace TradingStudio.Core.Factors;

/// <summary>
/// 因子合成器 — Wesley Gray《Quantitative Momentum》的核心方法。
///
/// 将多个因子合成为一个信号。Gray 的核心观点:
///   "单个因子是脆弱的，但 3-5 个低相关因子的组合是稳健的。"
///
/// 四种合成模式:
///   Equal     — 等权平均，最朴素但最不容易过拟合
///   VolInv    — 波动率倒数加权，低波动因子权重更高（Gray 推荐）
///   ICAgg     — IC 加权，历史预测能力强的因子权重更高（需要外部输入 IC 值）
///   Agreement — 一致性投票，仅当 N/M 个因子方向一致时才发出信号
/// </summary>
public class FactorComposite
{
    private readonly CompositeConfig _config;
    private readonly List<(IFactor Factor, double Weight)> _factors = new();
    private readonly Dictionary<string, double> _icWeights = new();

    public FactorComposite(CompositeConfig? config = null)
    {
        _config = config ?? new CompositeConfig();
    }

    public IReadOnlyList<IFactor> Factors => _factors.Select(f => f.Factor).ToList();

    /// <summary>注册因子（等权模式）</summary>
    public void AddFactor(IFactor factor)
    {
        _factors.Add((factor, 1.0));
        NormalizeWeights();
    }

    /// <summary>注册因子并指定权重</summary>
    public void AddFactor(IFactor factor, double weight)
    {
        _factors.Add((factor, Math.Max(0, weight)));
        NormalizeWeights();
    }

    /// <summary>设置 IC 加权模式下的因子权重（外部传入，通常来自回测分析）</summary>
    public void SetICWeights(Dictionary<string, double> icValues)
    {
        _icWeights.Clear();
        foreach (var (name, ic) in icValues)
        {
            // IC 可能为负（反向预测）→ 取绝对值作为权重
            _icWeights[name] = Math.Abs(ic);
        }
    }

    /// <summary>
    /// 合成所有因子的当前信号。
    /// 返回 CompositeResult，包含合成信号 + 各因子贡献明细。
    /// </summary>
    public CompositeResult Evaluate()
    {
        if (_factors.Count == 0)
            return new CompositeResult { IsEmpty = true };

        var factorValues = new List<FactorContribution>();

        foreach (var (factor, baseWeight) in _factors)
        {
            if (!factor.IsReady || !factor.LastValue.IsValid)
                continue;

            var weight = ComputeWeight(factor, baseWeight);
            factorValues.Add(new FactorContribution
            {
                FactorName = factor.Name,
                Category = factor.Category,
                Signal = factor.LastValue.SignalStrength,
                ZScore = factor.LastValue.ZScore,
                Percentile = factor.LastValue.Percentile,
                Weight = weight,
                WeightedSignal = factor.LastValue.SignalStrength * weight,
            });
        }

        if (factorValues.Count == 0)
            return new CompositeResult { IsEmpty = true };

        // 一致性过滤（Agreement 模式）
        if (_config.Mode == CompositeMode.Agreement)
        {
            var positiveCount = factorValues.Count(f => f.Signal > 0.05);
            var negativeCount = factorValues.Count(f => f.Signal < -0.05);
            var agreementRatio = Math.Max(positiveCount, negativeCount) / (double)factorValues.Count;

            if (agreementRatio < _config.AgreementThreshold)
            {
                return new CompositeResult
                {
                    CompositeSignal = 0,
                    Confidence = 0,
                    AgreementRatio = agreementRatio,
                    FactorCount = factorValues.Count,
                    AgreeingFactors = 0,
                    Contributions = factorValues,
                    Verdict = "因子分歧过大，不交易",
                };
            }
        }

        // 加权合成
        var totalWeight = factorValues.Sum(f => f.Weight);
        var compositeSignal = totalWeight > 0
            ? factorValues.Sum(f => f.WeightedSignal) / totalWeight
            : 0;

        // 置信度 = |信号强度| × 因子一致度
        var signalMagnitude = Math.Abs(compositeSignal);
        var agreeingDirections = factorValues.Count(f => Math.Sign(f.Signal) == Math.Sign(compositeSignal));
        var confidence = signalMagnitude * (agreeingDirections / (double)factorValues.Count);

        return new CompositeResult
        {
            CompositeSignal = Math.Clamp(compositeSignal, -1, 1),
            Confidence = Math.Clamp(confidence, 0, 1),
            AgreementRatio = agreeingDirections / (double)factorValues.Count,
            FactorCount = factorValues.Count,
            AgreeingFactors = agreeingDirections,
            Contributions = factorValues,
            Verdict = compositeSignal switch
            {
                >= 0.5 => $"强做多信号 ({agreeingDirections}/{factorValues.Count} 因子一致, 置信度 {confidence:P0})",
                >= 0.2 => $"弱做多信号 ({agreeingDirections}/{factorValues.Count} 因子一致)",
                <= -0.5 => $"强做空信号 ({agreeingDirections}/{factorValues.Count} 因子一致, 置信度 {confidence:P0})",
                <= -0.2 => $"弱做空信号 ({agreeingDirections}/{factorValues.Count} 因子一致)",
                _ => $"中性/不确定 ({agreeingDirections}/{factorValues.Count} 因子方向不一致)",
            },
        };
    }

    /// <summary>
    /// 批量评估 —— 对多个品种同时计算合成信号。
    /// 每个品种有自己的一组因子实例。
    /// </summary>
    public Dictionary<string, CompositeResult> EvaluateBatch(
        Dictionary<string, FactorComposite> instrumentComposites)
    {
        var results = new Dictionary<string, CompositeResult>();
        foreach (var (instId, composite) in instrumentComposites)
            results[instId] = composite.Evaluate();
        return results;
    }

    // ── 权重计算 ──

    private double ComputeWeight(IFactor factor, double baseWeight)
    {
        return _config.Mode switch
        {
            CompositeMode.Equal => baseWeight,
            CompositeMode.VolInv => ComputeVolInvWeight(factor, baseWeight),
            CompositeMode.ICAgg => ComputeICWeight(factor, baseWeight),
            CompositeMode.Agreement => baseWeight, // 一致性模式用等权
            _ => baseWeight,
        };
    }

    /// <summary>波动率倒数加权: 最近 N 期波动越低的因子权重越高</summary>
    private double ComputeVolInvWeight(IFactor factor, double baseWeight)
    {
        // 简化实现：用 z-score 的稳定性作为代理
        // |z-score| 越接近 1.0 → 更稳定的信号 → 更高权重
        // |z-score| > 3.0 → 极端信号 → 降低权重（可能是噪声）
        var absZ = Math.Abs(factor.LastValue.ZScore);
        var stabilityWeight = absZ switch
        {
            <= 1.0 => 1.5,    // 正常范围内的信号加权
            <= 2.0 => 1.0,   // 标准权重
            <= 3.0 => 0.5,   // 接近极端 → 降权
            _ => 0.25,         // 极端信号可能是噪声
        };
        return baseWeight * stabilityWeight;
    }

    /// <summary>IC 加权: 历史 IC 越高的因子权重越高</summary>
    private double ComputeICWeight(IFactor factor, double baseWeight)
    {
        if (_icWeights.TryGetValue(factor.Name, out var icWeight))
            return baseWeight * icWeight;
        return baseWeight; // 没有 IC 数据 → 等权
    }

    private void NormalizeWeights()
    {
        var total = _factors.Sum(f => f.Weight);
        if (total > 0)
        {
            for (int i = 0; i < _factors.Count; i++)
            {
                _factors[i] = (_factors[i].Factor, _factors[i].Weight / total);
            }
        }
    }
}

/// <summary>合成配置</summary>
public class CompositeConfig
{
    /// <summary>合成模式</summary>
    public CompositeMode Mode { get; init; } = CompositeMode.Equal;

    /// <summary>一致性阈值（仅 Agreement 模式有效）。
    /// 需要至少 N/M 的因子方向一致才产生信号。默认 0.67 (2/3)</summary>
    public double AgreementThreshold { get; init; } = 0.67;
}

/// <summary>合成模式</summary>
public enum CompositeMode
{
    /// <summary>等权平均 — 最朴素，最不容易过拟合</summary>
    Equal,

    /// <summary>波动率倒数加权 — Gray 推荐</summary>
    VolInv,

    /// <summary>IC 加权 — 需要外部回测提供因子 IC</summary>
    ICAgg,

    /// <summary>一致性投票 — 保守，仅在多因子方向一致时产生信号</summary>
    Agreement,
}

/// <summary>单个因子的信号贡献</summary>
public class FactorContribution
{
    public string FactorName { get; init; } = "";
    public string Category { get; init; } = "";
    public double Signal { get; init; }
    public double ZScore { get; init; }
    public double Percentile { get; init; }
    public double Weight { get; init; }
    public double WeightedSignal { get; init; }
}

/// <summary>因子合成结果</summary>
public class CompositeResult
{
    public bool IsEmpty { get; init; }

    /// <summary>合成信号 [-1, +1]。+1=强烈做多，-1=强烈做空，0=中性</summary>
    public double CompositeSignal { get; init; }

    /// <summary>置信度 [0, 1]。信号越强 + 因子越一致 → 置信度越高</summary>
    public double Confidence { get; init; }

    /// <summary>因子方向一致比例</summary>
    public double AgreementRatio { get; init; }

    public int FactorCount { get; init; }
    public int AgreeingFactors { get; init; }
    public List<FactorContribution> Contributions { get; init; } = [];
    public string Verdict { get; init; } = "";

    /// <summary>
    /// 将合成信号映射为仓位比例。
    /// compositeSignal = 0 → position = 0
    /// compositeSignal = ±1 → position = ±maxRatio
    /// </summary>
    public double ToPositionRatio(double maxRatio = 0.20)
    {
        return CompositeSignal * maxRatio * Confidence;
    }
}
