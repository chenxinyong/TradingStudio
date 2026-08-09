namespace TradingStudio.Core.Engine;

/// <summary>
/// 策略发出的交易意向 — 不含手数，仅表达方向+信心。
/// 策略只负责到这里，仓位计算（PositionSizing）由组合层统一处理。
///
/// 设计原则：信号和仓位是两个独立的关注点。
/// 详见 docs/design/18-trade-signal-portfolio-target-decoupling.md
/// </summary>
public record TradeSignal
{
    /// <summary>策略ID</summary>
    public string StrategyId { get; init; } = "";

    /// <summary>品种代码</summary>
    public string InstrumentId { get; init; } = "";

    /// <summary>方向意向</summary>
    public SignalDirection Direction { get; init; }

    /// <summary>信号强度 [0, 1]。0=微弱, 1=极强。用于复合信号加权和排序。</summary>
    public double Conviction { get; init; } = 0.5;

    /// <summary>信号来源说明（人工可读）</summary>
    public string Reason { get; init; } = "";

    /// <summary>信号时间戳</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>可选: 策略建议的止损价位（组合层可覆盖）</summary>
    public double? SuggestedStop { get; init; }

    /// <summary>可选: 策略建议的止盈价位（组合层可覆盖）</summary>
    public double? SuggestedTarget { get; init; }

    /// <summary>可选: 期望的最大持仓权重 (占组合 %)</summary>
    public double? MaxWeight { get; init; }

    /// <summary>策略优先级 (越小越优先，用于冲突消解)</summary>
    public int Priority { get; init; }
}

/// <summary>信号方向</summary>
public enum SignalDirection
{
    /// <summary>做多意向</summary>
    Long,
    /// <summary>做空意向</summary>
    Short,
    /// <summary>平仓意向（全平）</summary>
    Flat,
    /// <summary>减仓意向（保留部分）</summary>
    Reduce,
}
