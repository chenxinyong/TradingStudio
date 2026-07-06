namespace TradingStudio.Core.Risk;

/// <summary>
/// 凯利仓位计算器 — Ernie Chan Ch6: 资金管理和风险管理。
///
/// 凯利公式 (连续复利版):
///   f* = (μ - r) / σ²
///
///   其中 μ=期望收益率, r=无风险利率, σ=收益率标准差
///
/// Chan 的实操建议：
///   - 永远不要用满 Kelly — 参数估计有误差
///   - 1/2 Kelly: 牺牲少量收益，大幅降低回撤（推荐）
///   - 1/4 Kelly: 更保守，适合实盘初期
///   - 配合单笔风险上限（1-2%权益）和总仓位上限
///
/// 用法：
///   1. 从回测/实盘交易记录计算 μ 和 σ（年化）
///   2. 调用 Calculate() 得到最优仓位比例
///   3. 用最优仓位 × 账户权益 → 总风险敞口
///   4. 总风险敞口 ÷ 单笔风险 → 每笔交易的仓位
/// </summary>
public class KellyPositionSizer
{
    private readonly KellyConfig _config;

    public KellyPositionSizer(KellyConfig? config = null)
    {
        _config = config ?? new KellyConfig();
    }

    /// <summary>
    /// 从策略的历史收益率序列计算凯利最优仓位。
    /// </summary>
    /// <param name="periodReturns">每期收益率序列（日/周/月，保持一致即可）</param>
    /// <param name="periodsPerYear">年化周期数（日=252, 周=52, 月=12）</param>
    public KellyResult CalculateFromReturns(IReadOnlyList<double> periodReturns, int periodsPerYear = 252)
    {
        if (periodReturns.Count < 30)
            return new KellyResult { IsInsufficientData = true, Message = "数据不足（<30 期），凯利估计不可靠" };

        // 年化收益率 (μ)
        var meanReturn = periodReturns.Average();
        var annualReturn = meanReturn * periodsPerYear;

        // 年化波动率 (σ)
        var variance = periodReturns.Average(r => Math.Pow(r - meanReturn, 2));
        var annualStd = Math.Sqrt(variance) * Math.Sqrt(periodsPerYear);

        // 凯利最优 f*
        var riskFreeRate = _config.RiskFreeRate;
        var excessReturn = annualReturn - riskFreeRate;

        double fullKelly;
        if (annualStd > 0 && excessReturn > 0)
            fullKelly = excessReturn / (annualStd * annualStd);
        else
            fullKelly = 0;

        // 应用分数凯利
        var fractionalKelly = fullKelly * _config.KellyFraction;

        // 应用上限
        var cappedKelly = Math.Min(fractionalKelly, _config.MaxPositionRatio);

        // 单笔风险金额
        var singleTradeRisk = cappedKelly * _config.RiskPerTradeRatio;

        return new KellyResult
        {
            AnnualReturn = annualReturn,
            AnnualStd = annualStd,
            SharpeRatio = annualStd > 0 ? excessReturn / annualStd : 0,
            FullKelly = fullKelly,
            FractionalKelly = fractionalKelly,
            RecommendedPositionRatio = cappedKelly,
            SingleTradeRiskRatio = singleTradeRisk,
            KellyFraction = _config.KellyFraction,
            SampleSize = periodReturns.Count,
            PeriodsPerYear = periodsPerYear,
            Message = cappedKelly switch
            {
                0 => "凯利为负或零——策略期望收益不显著，建议不交易或极小仓位试错",
                <= 0.05 => "凯利仓位极低——策略边缘利润率，建议小仓位 + 严格止损",
                <= 0.20 => "凯利仓位适中——正常交易",
                <= 0.40 => "凯利仓位较高——策略表现好，但注意参数稳定性",
                _ => "凯利仓位很高——理论上可以重仓，但 Chan 建议不超过 20%"
            },
        };
    }

    /// <summary>
    /// 从胜率和盈亏比计算凯利（离散版）。
    /// 适合没有完整收益率序列时快速估算。
    ///
    /// 离散凯利: f* = (p·b - q) / b
    ///   p = 胜率, q = 1-p, b = 盈亏比 (平均盈利/平均亏损)
    /// </summary>
    public KellyResult CalculateFromWinLoss(double winRate, double profitLossRatio)
    {
        if (winRate < 0 || winRate > 1 || profitLossRatio <= 0)
            return new KellyResult { Message = "无效的胜率或盈亏比" };

        var p = winRate;
        var q = 1 - p;
        var b = profitLossRatio;

        var fullKelly = (p * b - q) / b;
        if (fullKelly < 0) fullKelly = 0;

        var fractionalKelly = fullKelly * _config.KellyFraction;
        var cappedKelly = Math.Min(fractionalKelly, _config.MaxPositionRatio);
        var singleTradeRisk = cappedKelly * _config.RiskPerTradeRatio;

        return new KellyResult
        {
            FullKelly = fullKelly,
            FractionalKelly = fractionalKelly,
            RecommendedPositionRatio = cappedKelly,
            SingleTradeRiskRatio = singleTradeRisk,
            KellyFraction = _config.KellyFraction,
            Message = cappedKelly switch
            {
                0 => "p·b - q ≤ 0——策略期望值为负，不应交易",
                <= 0.10 => "凯利仓位适中",
                _ => $"凯利 = {fullKelly:P1}（满凯利），建议用 {_config.KellyFraction:P0} 凯利 = {fractionalKelly:P1}"
            },
        };
    }

    /// <summary>
    /// 计算给定账户权益下的实际交易手数。
    /// </summary>
    public int CalculateLots(
        decimal accountEquity,
        double instrumentPrice,
        double multiplier,
        double marginRate,
        KellyResult kellyResult)
    {
        if (kellyResult.RecommendedPositionRatio <= 0) return 0;

        var riskCapital = (double)accountEquity * kellyResult.RecommendedPositionRatio;
        var contractValue = instrumentPrice * multiplier;
        var marginPerLot = contractValue * marginRate;

        if (marginPerLot <= 0) return 0;

        var lots = (int)(riskCapital / marginPerLot);
        return Math.Max(0, lots);
    }
}

/// <summary>凯利配置</summary>
public class KellyConfig
{
    /// <summary>无风险利率（默认 2.5%）</summary>
    public double RiskFreeRate { get; init; } = 0.025;

    /// <summary>
    /// 凯利分数 — Chan 建议 0.25 (1/4) 或 0.50 (1/2)。
    /// 0.50 = 半凯利（推荐），0.25 = 四分之一凯利（保守）。
    /// </summary>
    public double KellyFraction { get; init; } = 0.50;

    /// <summary>最大仓位比例上限 — 即使凯利说 50%，也不超过这个值</summary>
    public double MaxPositionRatio { get; init; } = 0.20;

    /// <summary>单笔交易风险占推荐仓位的比例（默认 10%，即单笔风险 = 总仓位 × 10%）</summary>
    public double RiskPerTradeRatio { get; init; } = 0.10;
}

/// <summary>凯利计算结果</summary>
public class KellyResult
{
    public bool IsInsufficientData { get; init; }
    public double AnnualReturn { get; init; }
    public double AnnualStd { get; init; }
    public double SharpeRatio { get; init; }

    /// <summary>满凯利最优仓位比例</summary>
    public double FullKelly { get; init; }

    /// <summary>分数凯利（FullKelly × KellyFraction）</summary>
    public double FractionalKelly { get; init; }

    /// <summary>推荐仓位比例（FractionalKelly 经上限裁剪后）</summary>
    public double RecommendedPositionRatio { get; init; }

    /// <summary>单笔交易风险占权益的比例</summary>
    public double SingleTradeRiskRatio { get; init; }

    public double KellyFraction { get; init; }
    public int SampleSize { get; init; }
    public int PeriodsPerYear { get; init; }
    public string Message { get; init; } = "";
}
