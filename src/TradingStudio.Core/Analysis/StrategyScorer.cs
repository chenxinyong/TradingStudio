namespace TradingStudio.Core.Analysis;

/// <summary>
/// 多维度策略评分器 — Ernie Chan Ch3.4: "不要只看 Sharpe"。
///
/// Chan 的观点：单个指标可以被操纵（过拟合到高 Sharpe），
/// 但多维度评分暴露策略的真实质量。
///
/// 评分维度：
///   收益类: Sharpe, Sortino, CAGR, Calmar
///   风险类: MaxDD, 最长回撤恢复期
///   交易类: 胜率, 盈亏比, 交易频率
///   稳健类: OOS/IS稳定性比率 (来自 WalkForwardValidator)
///
/// 权重可配置，默认按 Chan 的建议倾向于稳健性 + 风险调整收益。
/// </summary>
public class StrategyScorer
{
    private readonly ScorerConfig _config;

    public StrategyScorer(ScorerConfig? config = null)
    {
        _config = config ?? new ScorerConfig();
    }

    /// <summary>
    /// 计算策略的综合评分 (0-100)。
    /// 每个维度先归一化到 [0,1]，再按权重加权。
    /// </summary>
    public StrategyScore Score(StrategyScoreInput input)
    {
        var scores = new ScoreBreakdown();

        // ═══ 收益类 ═══
        scores.SharpeScore = NormalizeSharpe(input.SharpeRatio);
        scores.SortinoScore = NormalizeSharpe(input.SortinoRatio);
        scores.CagrScore = NormalizeCAGR(input.CAGR);
        scores.CalmarScore = NormalizeCalmar(input.CalmarRatio);

        // ═══ 风险类 ═══
        scores.DrawdownScore = NormalizeDrawdown(input.MaxDrawdown);
        scores.RecoveryScore = NormalizeRecovery(input.RecoveryDays);

        // ═══ 交易类 ═══
        scores.WinRateScore = NormalizeWinRate(input.WinRate);
        scores.PLRScore = NormalizePLR(input.ProfitLossRatio);
        scores.FrequencyScore = NormalizeFrequency(input.TradesPerYear);

        // ═══ 稳健类 ═══
        scores.StabilityScore = NormalizeStability(input.OosIsStabilityRatio);
        scores.PositiveWindowScore = input.OosPositiveWindowRatio;

        // ═══ 惩罚项 ═══
        scores.Penalty = CalculatePenalty(input);

        // 加权综合
        var totalWeight = _config.SharpeWeight + _config.SortinoWeight + _config.CagrWeight +
                          _config.CalmarWeight + _config.DrawdownWeight + _config.RecoveryWeight +
                          _config.WinRateWeight + _config.PLRWeight + _config.FrequencyWeight +
                          _config.StabilityWeight + _config.PositiveWindowWeight;

        var composite = (
            scores.SharpeScore * _config.SharpeWeight +
            scores.SortinoScore * _config.SortinoWeight +
            scores.CagrScore * _config.CagrWeight +
            scores.CalmarScore * _config.CalmarWeight +
            scores.DrawdownScore * _config.DrawdownWeight +
            scores.RecoveryScore * _config.RecoveryWeight +
            scores.WinRateScore * _config.WinRateWeight +
            scores.PLRScore * _config.PLRWeight +
            scores.FrequencyScore * _config.FrequencyWeight +
            scores.StabilityScore * _config.StabilityWeight +
            scores.PositiveWindowScore * _config.PositiveWindowWeight
        ) / totalWeight;

        // 应用惩罚
        composite = Math.Max(0, composite - scores.Penalty);

        // 缩放到 0-100
        scores.Composite = composite * 100;
        scores.Grade = scores.Composite switch
        {
            >= 75 => StrategyGrade.A,
            >= 60 => StrategyGrade.B,
            >= 45 => StrategyGrade.C,
            >= 30 => StrategyGrade.D,
            _ => StrategyGrade.F,
        };

        return new StrategyScore
        {
            Composite = scores.Composite,
            Grade = scores.Grade,
            Breakdown = scores,
            Warnings = GenerateWarnings(input, scores),
        };
    }

    // ── 归一化函数（把原始指标映射到 [0,1]）──

    /// <summary>Sharpe/Sortino: 0→0, 1.0→0.5, 2.0→0.8, 3.0→1.0</summary>
    private static double NormalizeSharpe(double sharpe)
    {
        if (double.IsNaN(sharpe) || double.IsInfinity(sharpe)) return 0;
        // sigmoid 映射：在大多数策略 Sharpe 范围内线性
        return Math.Clamp(sharpe / 3.0, 0, 1.0);
    }

    /// <summary>CAGR: 0→0, 20%→0.5, 50%→1.0</summary>
    private static double NormalizeCAGR(double cagr)
    {
        if (double.IsNaN(cagr)) return 0;
        return Math.Clamp(cagr / 0.50, 0, 1.0); // 50%年化=满分
    }

    /// <summary>Calmar (CAGR/MaxDD): 0→0, 1.0→0.5, 2.0→0.8, 3.0→1.0</summary>
    private static double NormalizeCalmar(double calmar)
    {
        if (double.IsNaN(calmar) || double.IsInfinity(calmar)) return 0;
        return Math.Clamp(calmar / 3.0, 0, 1.0);
    }

    /// <summary>
    /// MaxDD: 分数随回撤增大而快速衰减。
    /// -5%→0.9, -10%→0.75, -20%→0.5, -30%→0.25, -50%→0
    /// </summary>
    private static double NormalizeDrawdown(double maxDrawdown)
    {
        if (double.IsNaN(maxDrawdown)) return 0;
        // MaxDD 是负数（-0.20 = 20% 回撤）
        var dd = Math.Abs(maxDrawdown);
        // 指数衰减: e^(-3*dd)，-10%→0.74, -20%→0.55, -30%→0.41
        return Math.Clamp(Math.Exp(-3.0 * dd), 0, 1.0);
    }

    /// <summary>回撤恢复期: <30天→满分, >1年→零分</summary>
    private static double NormalizeRecovery(int recoveryDays)
    {
        if (recoveryDays <= 0) return 0.5; // 没有回撤过
        return Math.Clamp(1.0 - (recoveryDays / 365.0), 0, 1.0);
    }

    /// <summary>胜率: 30%→0, 50%→0.7, 60%→1.0</summary>
    private static double NormalizeWinRate(double winRate)
    {
        if (double.IsNaN(winRate)) return 0;
        // Chan: 胜率不是最重要的，但极低胜率(<30%)的策略心理压力大
        return Math.Clamp((winRate - 0.30) / 0.30, 0, 1.0);
    }

    /// <summary>盈亏比: 1.0→0, 1.5→0.5, 2.0→0.7, 3.0→1.0</summary>
    private static double NormalizePLR(double plr)
    {
        if (double.IsNaN(plr) || double.IsInfinity(plr)) return 0;
        return Math.Clamp((plr - 1.0) / 2.0, 0, 1.0);
    }

    /// <summary>
    /// 交易频率: Chan 认为太少和太多都不好。
    /// 太少→样本小、统计不可靠；太多→过拟合风险高。
    /// 最佳区间: 年化 20-100 笔 → 满分
    /// </summary>
    private static double NormalizeFrequency(int tradesPerYear)
    {
        if (tradesPerYear < 5) return 0;    // 样本太小
        if (tradesPerYear <= 20) return 0.5 + (tradesPerYear - 5) / 15.0 * 0.5;
        if (tradesPerYear <= 100) return 1.0; // 最佳区间
        if (tradesPerYear <= 300) return 1.0 - (tradesPerYear - 100) / 200.0 * 0.5;
        return 0.5; // >300笔/年可能过拟合
    }

    /// <summary>OOS/IS 稳定性比率: 0.5→0, 0.7→0.6, 1.0→1.0</summary>
    private static double NormalizeStability(double stabilityRatio)
    {
        if (double.IsNaN(stabilityRatio)) return 0;
        return Math.Clamp((stabilityRatio - 0.3) / 0.7, 0, 1.0);
    }

    /// <summary>
    /// 计算惩罚项。下列情况扣除分数：
    /// - 最大回撤超过 40%
    /// - 胜率低于 30%
    /// - 交易次数太少 (<10)
    /// - OOS Sharpe 为负
    /// </summary>
    private double CalculatePenalty(StrategyScoreInput input)
    {
        double penalty = 0;

        if (Math.Abs(input.MaxDrawdown) > 0.40)
            penalty += _config.ExtremeDrawdownPenalty;

        if (input.WinRate < 0.30 && input.TotalTrades > 20)
            penalty += _config.LowWinRatePenalty;

        if (input.TotalTrades < 10)
            penalty += _config.LowTradeCountPenalty;

        if (input.OosSharpeMean < 0)
            penalty += _config.NegativeOosSharpePenalty;

        return penalty;
    }

    private static List<string> GenerateWarnings(StrategyScoreInput input, ScoreBreakdown scores)
    {
        var warnings = new List<string>();

        if (Math.Abs(input.MaxDrawdown) > 0.30)
            warnings.Add($"最大回撤 {input.MaxDrawdown:P1} 超过30%——评估心理承受能力");
        if (input.WinRate < 0.35 && input.TotalTrades > 20)
            warnings.Add($"胜率仅 {input.WinRate:P0}——可能经历长时间连续亏损");
        if (input.OosIsStabilityRatio < 0.5)
            warnings.Add($"OOS/IS稳定性 {input.OosIsStabilityRatio:F2} < 0.5——强烈过拟合迹象");
        if (input.TotalTrades < 20)
            warnings.Add("交易次数不足20笔——统计结论不可靠");
        if (input.OosSharpeMean < 0)
            warnings.Add("OOS Sharpe 均值为负——策略在样本外不赚钱");

        return warnings;
    }
}

/// <summary>评分器配置（按 Chan 的建议默认值）</summary>
public class ScorerConfig
{
    // 收益类权重
    public double SharpeWeight { get; init; } = 1.5;    // Chan: 核心指标
    public double SortinoWeight { get; init; } = 1.0;    // Chan: 下行风险更重要
    public double CagrWeight { get; init; } = 0.5;
    public double CalmarWeight { get; init; } = 0.8;     // Chan: "如果只能看两个指标，Sharpe+Calmar"

    // 风险类权重
    public double DrawdownWeight { get; init; } = 1.2;   // Chan: MaxDD 和 Sharpe 同等重要
    public double RecoveryWeight { get; init; } = 0.5;

    // 交易类权重
    public double WinRateWeight { get; init; } = 0.3;    // Chan: 胜率不重要，盈亏比重要
    public double PLRWeight { get; init; } = 0.8;
    public double FrequencyWeight { get; init; } = 0.4;

    // 稳健类权重（来自 WalkForwardValidator）
    public double StabilityWeight { get; init; } = 1.5;  // Chan: 最重要的指标
    public double PositiveWindowWeight { get; init; } = 1.0;

    // 惩罚项
    public double ExtremeDrawdownPenalty { get; init; } = 0.15;
    public double LowWinRatePenalty { get; init; } = 0.10;
    public double LowTradeCountPenalty { get; init; } = 0.20;
    public double NegativeOosSharpePenalty { get; init; } = 0.30;
}

/// <summary>评分输入 — 从 PerformanceReport + WalkForwardSummary 组装</summary>
public class StrategyScoreInput
{
    // 收益
    public double SharpeRatio { get; init; }
    public double SortinoRatio { get; init; }
    public double CAGR { get; init; }           // 小数 (0.20 = 20%)
    public double CalmarRatio { get; init; }    // CAGR / |MaxDD|

    // 风险
    public double MaxDrawdown { get; init; }    // 负数 (-0.20 = 20%)
    public int RecoveryDays { get; init; }      // 最长回撤恢复天数

    // 交易
    public double WinRate { get; init; }        // 小数 (0.45 = 45%)
    public double ProfitLossRatio { get; init; } // 盈亏比
    public int TradesPerYear { get; init; }
    public int TotalTrades { get; init; }

    // 稳健 (来自 WalkForwardValidator)
    public double OosIsStabilityRatio { get; init; }   // OOS/IS Sharpe
    public double OosPositiveWindowRatio { get; init; } // OOS 正 Sharpe 窗口比例
    public double OosSharpeMean { get; init; }          // OOS Sharpe 均值
}

/// <summary>各维度得分明细</summary>
public class ScoreBreakdown
{
    public double SharpeScore { get; set; }
    public double SortinoScore { get; set; }
    public double CagrScore { get; set; }
    public double CalmarScore { get; set; }
    public double DrawdownScore { get; set; }
    public double RecoveryScore { get; set; }
    public double WinRateScore { get; set; }
    public double PLRScore { get; set; }
    public double FrequencyScore { get; set; }
    public double StabilityScore { get; set; }
    public double PositiveWindowScore { get; set; }
    public double Penalty { get; set; }
    public double Composite { get; set; }
    public StrategyGrade Grade { get; set; }
}

/// <summary>综合评分结果</summary>
public class StrategyScore
{
    public double Composite { get; init; }
    public StrategyGrade Grade { get; init; }
    public ScoreBreakdown Breakdown { get; init; } = new();
    public List<string> Warnings { get; init; } = [];

    /// <summary>生成适合 Obsidian 策略文档的 Markdown 评分表</summary>
    public string ToMarkdown()
    {
        var gradeIcon = Grade switch
        {
            StrategyGrade.A => "🟢",
            StrategyGrade.B => "🔵",
            StrategyGrade.C => "🟡",
            StrategyGrade.D => "🟠",
            StrategyGrade.F => "🔴",
            _ => "⚪",
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## 综合评分: {Composite:F1}/100 {gradeIcon} ({Grade})");
        sb.AppendLine();
        sb.AppendLine("| 维度 | 得分 | 指标值 |");
        sb.AppendLine("|------|:----:|--------|");
        sb.AppendLine($"| Sharpe | {Breakdown.SharpeScore:P0} | — |");
        sb.AppendLine($"| Sortino | {Breakdown.SortinoScore:P0} | — |");
        sb.AppendLine($"| Calmar | {Breakdown.CalmarScore:P0} | — |");
        sb.AppendLine($"| 回撤控制 | {Breakdown.DrawdownScore:P0} | — |");
        sb.AppendLine($"| 胜率 | {Breakdown.WinRateScore:P0} | — |");
        sb.AppendLine($"| 盈亏比 | {Breakdown.PLRScore:P0} | — |");
        sb.AppendLine($"| 交易频率 | {Breakdown.FrequencyScore:P0} | — |");
        sb.AppendLine($"| OOS稳定性 | {Breakdown.StabilityScore:P0} | — |");
        sb.AppendLine($"| 惩罚 | -{Breakdown.Penalty:P0} | — |");

        if (Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### ⚠️ 警告");
            foreach (var w in Warnings)
                sb.AppendLine($"- {w}");
        }

        return sb.ToString();
    }
}

public enum StrategyGrade { A, B, C, D, F }
