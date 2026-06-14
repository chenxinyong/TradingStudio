using TradingStudio.Core.Engine;

namespace TradingStudio.Engine.Statistics;

/// <summary>单策略绩效报告</summary>
public class PerformanceReport
{
    public string StrategyId { get; init; } = "";
    public decimal StartingCapital { get; init; }
    public decimal FinalEquity { get; init; }
    public decimal TotalNetProfit { get; init; }
    public decimal CompoundingAnnualReturn { get; init; }
    public decimal MaxDrawdown { get; init; }
    public decimal SharpeRatio { get; init; }
    public decimal SortinoRatio { get; init; }
    public int TotalOrders { get; init; }
    public int TotalTrades { get; init; }
    public decimal WinRate { get; init; }
    public decimal AverageWin { get; init; }
    public decimal AverageLoss { get; init; }
    public decimal ProfitLossRatio { get; init; }
    public decimal TotalFees { get; init; }
    public decimal TotalSlippage { get; init; }
    public List<(DateTimeOffset Time, decimal Equity)> EquityCurve { get; init; } = [];
    public List<Trade> Trades { get; init; } = [];

    public static PerformanceReport Generate(
        string strategyId,
        SubPortfolio subPortfolio,
        IReadOnlyList<Trade> trades,
        IReadOnlyList<(DateTimeOffset, decimal)> equityCurve)
    {
        var wins = trades.Where(t => t.PnL > 0).ToList();
        var losses = trades.Where(t => t.PnL <= 0).ToList();

        var maxDrawdown = DrawdownCalculator.CalculateMaxDrawdown(equityCurve);

        return new PerformanceReport
        {
            StrategyId = strategyId,
            StartingCapital = subPortfolio.AllocatedCapital,
            FinalEquity = subPortfolio.Equity,
            TotalNetProfit = subPortfolio.Equity - subPortfolio.AllocatedCapital,
            MaxDrawdown = (decimal)maxDrawdown,
            TotalTrades = trades.Count,
            WinRate = trades.Count > 0 ? (decimal)wins.Count / trades.Count : 0,
            AverageWin = wins.Count > 0 ? wins.Average(t => t.PnL) : 0,
            AverageLoss = losses.Count > 0 ? losses.Average(t => t.PnL) : 0,
            ProfitLossRatio = losses.Count > 0 && losses.Average(t => Math.Abs(t.PnL)) > 0
                ? wins.Count > 0 ? wins.Average(t => t.PnL) / losses.Average(t => Math.Abs(t.PnL)) : 0
                : 0,
            TotalFees = trades.Sum(t => t.Fee),
            TotalSlippage = trades.Sum(t => t.Slippage),
            EquityCurve = equityCurve.ToList(),
            Trades = trades.ToList(),
        };
    }
}
