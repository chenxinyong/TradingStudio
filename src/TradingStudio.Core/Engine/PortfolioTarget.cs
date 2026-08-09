namespace TradingStudio.Core.Engine;

/// <summary>
/// 组合层计算后的目标持仓 — 精确到手数和权重。
/// 由 ITargetCombiner 根据多个 TradeSignal 计算得出。
/// 当前持仓 vs 目标持仓的差异 → Rebalancer 生成订单。
///
/// 设计原则：可审计 — SourceStrategyIds 记录每个目标的信号来源。
/// 详见 docs/design/18-trade-signal-portfolio-target-decoupling.md
/// </summary>
public class PortfolioTarget
{
    /// <summary>品种代码</summary>
    public string InstrumentId { get; init; } = "";

    /// <summary>目标持仓手数: 正=多头, 负=空头, 0=空仓</summary>
    public int TargetQuantity { get; init; }

    /// <summary>目标占组合权重 [0, 1]</summary>
    public double TargetWeight { get; init; }

    /// <summary>生成此目标的信号来源策略ID列表（用于审计/调试）</summary>
    public List<string> SourceStrategyIds { get; init; } = new();

    /// <summary>合成信号强度 (多个策略信号的加权平均)</summary>
    public double CompositeConviction { get; init; }

    /// <summary>组合层设置的止损价</summary>
    public double? StopLoss { get; init; }

    /// <summary>组合层设置的止盈价</summary>
    public double? TakeProfit { get; init; }
}
