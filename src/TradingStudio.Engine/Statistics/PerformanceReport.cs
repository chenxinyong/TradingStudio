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
    /// <summary>退出原因统计: {reason: (tradeCount, totalPnL, winRate)}</summary>
    public Dictionary<string, ExitReasonStats> ExitReasonBreakdown { get; init; } = [];

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
        var cagr = CalculateCAGR(equityCurve, subPortfolio.AllocatedCapital, subPortfolio.Equity);

        return new PerformanceReport
        {
            StrategyId = strategyId,
            StartingCapital = subPortfolio.AllocatedCapital,
            FinalEquity = subPortfolio.Equity,
            TotalNetProfit = subPortfolio.Equity - subPortfolio.AllocatedCapital,
            CompoundingAnnualReturn = cagr,
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
            ExitReasonBreakdown = BuildExitReasonBreakdown(trades),
        };
    }

    private static Dictionary<string, ExitReasonStats> BuildExitReasonBreakdown(IReadOnlyList<Trade> trades)
    {
        var dict = new Dictionary<string, ExitReasonStats>();
        foreach (var t in trades)
        {
            var reason = string.IsNullOrEmpty(t.ExitReason) ? "Unknown" : t.ExitReason;
            if (!dict.TryGetValue(reason, out var s))
            {
                s = new ExitReasonStats { Reason = reason };
                dict[reason] = s;
            }
            s.Count++;
            s.TotalPnL += t.PnL;
            if (t.IsWin) s.Wins++;
        }
        foreach (var s in dict.Values)
            s.WinRate = s.Count > 0 ? (decimal)s.Wins / s.Count : 0;
        return dict;
    }

    public class ExitReasonStats
    {
        public string Reason { get; set; } = "";
        public int Count { get; set; }
        public int Wins { get; set; }
        public decimal WinRate { get; set; }
        public decimal TotalPnL { get; set; }
    }

    /// <summary>计算年化复合收益率 (CAGR)</summary>
    private static decimal CalculateCAGR(
        IReadOnlyList<(DateTimeOffset Time, decimal Equity)> equityCurve,
        decimal startCapital, decimal finalEquity)
    {
        if (equityCurve.Count < 2 || startCapital <= 0) return 0;
        var totalReturn = (double)(finalEquity / startCapital);
        if (totalReturn <= 0) return -1m;   // 穿仓/归零
        var years = (equityCurve[^1].Time - equityCurve[0].Time).TotalDays / 365.25;
        // 回测不足 1 天：年化无意义且 1/years 会让 Pow 数值爆炸(decimal 溢出) → 退回区间收益率
        if (years < 1.0 / 365.25) return (decimal)(totalReturn - 1.0);
        var cagr = Math.Pow(totalReturn, 1.0 / years) - 1.0;
        if (double.IsNaN(cagr) || double.IsInfinity(cagr)) return 0;
        return (decimal)Math.Clamp(cagr, -1.0, 1e6);   // 兜底防 decimal 溢出
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
