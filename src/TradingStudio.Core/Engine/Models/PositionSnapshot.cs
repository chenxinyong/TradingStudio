namespace TradingStudio.Core.Engine;

/// <summary>
/// 持仓不可变快照 —— IPortfolioState.GetPosition/AllPositions 的公开读面。
/// 从可变 <see cref="Position"/> 投影而来，外部只能读不能改，杜绝绕过 ProcessFill 直接改内部账本。
/// </summary>
public record PositionSnapshot
{
    public string InstrumentId { get; init; } = "";
    public string StrategyId { get; init; } = "";
    /// <summary>今仓（带符号净额，+ 多 - 空）。</summary>
    public int QuantityToday { get; init; }
    /// <summary>昨仓（带符号净额，+ 多 - 空）。</summary>
    public int QuantityYesterday { get; init; }
    /// <summary>净持仓手数：今仓 + 昨仓（+ 多, - 空）。</summary>
    public int Quantity => QuantityToday + QuantityYesterday;
    public decimal AvgPrice { get; init; }
    public double MarketPrice { get; init; }
    public double UnrealizedPnl { get; init; }
    public decimal Margin { get; init; }
    public decimal Commission { get; init; }
    public DateTimeOffset CreatedTime { get; init; }
    /// <summary>CTP PositionDate — '1'=今仓, '2'=昨仓, '\0'=未设置（回测模式）。平今/平昨判断的回退依据。</summary>
    public char PositionDate { get; init; } = '\0';
}
