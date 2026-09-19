using TradingStudio.Core.Engine;

namespace TradingStudio.Core.Risk;

/// <summary>
/// 引擎内部状态的只读快照。风控规则只能查询，不能修改。
/// </summary>
public interface IPortfolioState
{
    decimal Cash { get; }
    decimal Equity { get; }
    decimal MarginUsed { get; }
    decimal StartingCapital { get; }
    decimal PeakEquity { get; }
    decimal TodayPnL { get; }
    decimal TotalPnL { get; }
    PositionSnapshot? GetPosition(string strategyId, string instrumentId);
    IReadOnlyList<PositionSnapshot> AllPositions { get; }
    /// <summary>持仓对账状态。Mismatch 时风控拒绝新开仓（平仓放行）。默认实现供轻量 Mock 不强制实现。</summary>
    ReconcileStatus ReconcileStatus => ReconcileStatus.NotReconciled;
    IReadOnlyList<Order> ActiveOrders { get; }
    IReadOnlyList<Trade> TradeHistory { get; }
    IReadOnlyList<SubPortfolioState> SubPortfolios { get; }
}

/// <summary>策略分账只读快照</summary>
public record SubPortfolioState
{
    public string StrategyId { get; init; } = "";
    public decimal AllocatedCapital { get; init; }
    public decimal Equity { get; init; }
    public decimal PeakEquity { get; init; }
    public decimal TodayPnL { get; init; }
}
