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
        IReadOnlyList<(DateTimeOffset, decimal)> equityCurve,
        int totalOrders = 0)
    {
        var wins = trades.Where(t => t.PnL > 0).ToList();
        var losses = trades.Where(t => t.PnL <= 0).ToList();

        var maxDrawdown = DrawdownCalculator.CalculateMaxDrawdown(equityCurve);
        var (sharpe, sortino) = CalculateRatios(equityCurve);

        return new PerformanceReport
        {
            StrategyId = strategyId,
            StartingCapital = subPortfolio.AllocatedCapital,
            FinalEquity = subPortfolio.Equity,
            TotalNetProfit = subPortfolio.Equity - subPortfolio.AllocatedCapital,
            MaxDrawdown = (decimal)maxDrawdown,
            SharpeRatio = (decimal)sharpe,
            SortinoRatio = (decimal)sortino,
            TotalOrders = totalOrders > 0 ? totalOrders : trades.Count * 2, // 每个Trade至少一对买卖单
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

    /// <summary>从权益曲线计算年化 Sharpe 和 Sortino 比率</summary>
    private static (double Sharpe, double Sortino) CalculateRatios(
        IReadOnlyList<(DateTimeOffset Time, decimal Equity)> equityCurve)
    {
        if (equityCurve.Count < 2) return (0, 0);

        // 提取每日权益（按日期去重，取当日最后一条）
        var dailyEquity = equityCurve
            .GroupBy(p => p.Time.Date)
            .Select(g => (double)g.Last().Equity)
            .ToList();

        if (dailyEquity.Count < 2) return (0, 0);

        // 每日收益率
        var returns = new List<double>(dailyEquity.Count - 1);
        for (int i = 1; i < dailyEquity.Count; i++)
        {
            var prev = dailyEquity[i - 1];
            if (prev > 0)
                returns.Add(dailyEquity[i] / prev - 1.0);
        }

        if (returns.Count == 0) return (0, 0);

        var meanReturn = returns.Average();
        var stdDev = Math.Sqrt(returns.Average(r => Math.Pow(r - meanReturn, 2)));

        // 年化 Sharpe (假设 252 交易日，无风险利率 = 0)
        var sharpe = stdDev > 0 ? meanReturn / stdDev * Math.Sqrt(252) : 0;

        // Sortino: 只考虑下行波动
        var downReturns = returns.Where(r => r < 0).ToList();
        var downStdDev = downReturns.Count > 0
            ? Math.Sqrt(downReturns.Average(r => Math.Pow(r - meanReturn, 2)))
            : stdDev; // fallback to total stdDev if no negative returns

        var sortino = downStdDev > 0 ? meanReturn / downStdDev * Math.Sqrt(252) : 0;

        return (sharpe, sortino);
    }
}
