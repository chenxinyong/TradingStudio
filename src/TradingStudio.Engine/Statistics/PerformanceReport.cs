namespace TradingStudio.Engine.Statistics;

/// <summary>单策略绩效报告</summary>
public class PerformanceReport
{
    public string StrategyId { get; init; } = "";

    // 收益
    public decimal StartingCapital { get; init; }
    public decimal FinalEquity { get; init; }
    public decimal TotalNetProfit { get; init; }
    public decimal CompoundingAnnualReturn { get; init; }

    // 风险
    public decimal MaxDrawdown { get; init; }
    public decimal SharpeRatio { get; init; }
    public decimal SortinoRatio { get; init; }

    // 交易
    public int TotalOrders { get; init; }
    public int TotalTrades { get; init; }
    public decimal WinRate { get; init; }
    public decimal AverageWin { get; init; }
    public decimal AverageLoss { get; init; }
    public decimal ProfitLossRatio { get; init; }
    public decimal TotalFees { get; init; }
    public decimal TotalSlippage { get; init; }

    // 曲线
    public List<(DateTimeOffset Time, decimal Equity)> EquityCurve { get; init; } = [];
    public List<Core.Engine.Trade> Trades { get; init; } = [];
}
