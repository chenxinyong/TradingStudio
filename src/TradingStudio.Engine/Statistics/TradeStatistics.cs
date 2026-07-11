using TradingStudio.Core.Engine;

namespace TradingStudio.Engine.Statistics;

/// <summary>
/// 交易统计分析 — 从已完成交易列表中计算各类绩效指标。
/// 纯函数，无副作用，线程安全。
/// </summary>
public class TradeStatistics
{
    // ── 交易计数 ──
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }

    // ── 盈亏汇总 ──
    public decimal GrossProfit { get; init; }       // 所有盈利交易的总盈利
    public decimal GrossLoss { get; init; }          // 所有亏损交易的总亏损（正数）
    public decimal TotalNetProfit { get; init; }     // GrossProfit - GrossLoss
    public decimal ProfitFactor { get; init; }       // GrossProfit / GrossLoss, >1 盈利

    // ── 单笔极值 ──
    public decimal LargestWin { get; init; }
    public decimal LargestLoss { get; init; }

    // ── 连胜/连败 ──
    public int MaxConsecutiveWins { get; init; }
    public int MaxConsecutiveLosses { get; init; }

    // ── 胜率分析 ──
    public decimal WinRate { get; init; }            // 0.0 ~ 1.0
    public decimal AverageWin { get; init; }          // 平均每笔盈利
    public decimal AverageLoss { get; init; }         // 平均每笔亏损（正数表示）
    public decimal WinLossRatio { get; init; }        // AvgWin / AvgLoss

    // ── 期望值 ──
    public decimal Expectancy { get; init; }          // (WinRate × AvgWin) - ((1-WinRate) × AvgLoss)

    // ── 持仓时间 ──
    public TimeSpan AverageHoldingPeriod { get; init; }
    public TimeSpan LongestHoldingPeriod { get; init; }
    public TimeSpan ShortestHoldingPeriod { get; init; }

    // ── 回撤与恢复 ──
    public decimal MaxDrawdownPct { get; init; }      // 最大回撤百分比
    public decimal RecoveryFactor { get; init; }      // NetProfit / MaxDrawdown

    // ── 费用 ──
    public decimal TotalFees { get; init; }
    public decimal TotalSlippage { get; init; }
    public decimal NetProfitAfterCosts { get; init; }  // = TotalNetProfit（Trade.PnL 已扣费用、滑点已含在成交价，不重复扣）

    // ── 时间分布 ──
    public int ActiveDays { get; init; }               // 有交易的天数
    public double TradesPerDay { get; init; }          // 日均交易次数
    public Dictionary<string, MonthlyStats> MonthlyBreakdown { get; init; } = [];

    /// <summary>从交易列表计算所有统计指标</summary>
    public static TradeStatistics From(
        IReadOnlyList<Trade> trades,
        IReadOnlyList<(DateTimeOffset Time, decimal Equity)>? equityCurve = null)
    {
        if (trades.Count == 0)
            return new TradeStatistics { TotalTrades = 0 };

        var wins = trades.Where(t => t.PnL > 0).ToList();
        var losses = trades.Where(t => t.PnL <= 0).ToList();

        var grossProfit = wins.Sum(t => t.PnL);
        var grossLoss = Math.Abs(losses.Sum(t => t.PnL));
        var totalNet = trades.Sum(t => t.PnL);
        var totalFees = trades.Sum(t => t.Fee);
        var totalSlippage = trades.Sum(t => t.Slippage);

        // 连胜/连败
        var consecutive = CalculateConsecutiveStreaks(trades);

        // 持仓时间（只统计有完整 Entry/Exit 时间戳的交易）
        var holdings = trades
            .Where(t => t.EntryTime != default && t.ExitTime != default)
            .Select(t => t.ExitTime - t.EntryTime)
            .ToList();

        // 回撤
        var maxDrawdown = equityCurve is { Count: > 1 }
            ? DrawdownCalculator.CalculateMaxDrawdown(equityCurve)
            : 0;

        // 月度分布
        var monthly = trades
            .GroupBy(t => new { t.ExitTime.Year, t.ExitTime.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .ToDictionary(
                g => $"{g.Key.Year}-{g.Key.Month:D2}",
                g => new MonthlyStats
                {
                    Trades = g.Count(),
                    Wins = g.Count(t => t.IsWin),
                    NetPnL = g.Sum(t => t.PnL),
                    Fees = g.Sum(t => t.Fee),
                });

        // 活跃天数
        var activeDays = trades.Select(t => t.ExitTime.Date).Distinct().Count();

        return new TradeStatistics
        {
            TotalTrades = trades.Count,
            WinningTrades = wins.Count,
            LosingTrades = losses.Count,

            GrossProfit = decimal.Round(grossProfit, 2),
            GrossLoss = decimal.Round(grossLoss, 2),
            TotalNetProfit = decimal.Round(totalNet, 2),
            ProfitFactor = grossLoss > 0 ? decimal.Round(grossProfit / grossLoss, 2) : (grossProfit > 0 ? decimal.MaxValue : 0),

            LargestWin = wins.Count > 0 ? wins.Max(t => t.PnL) : 0,
            LargestLoss = losses.Count > 0 ? losses.Min(t => t.PnL) : 0,

            MaxConsecutiveWins = consecutive.Wins,
            MaxConsecutiveLosses = consecutive.Losses,

            WinRate = trades.Count > 0 ? decimal.Round((decimal)wins.Count / trades.Count, 4) : 0,
            AverageWin = wins.Count > 0 ? decimal.Round(wins.Average(t => t.PnL), 2) : 0,
            AverageLoss = losses.Count > 0 ? decimal.Round(Math.Abs(losses.Average(t => t.PnL)), 2) : 0,
            WinLossRatio = losses.Count > 0 && losses.Average(t => Math.Abs(t.PnL)) > 0
                ? decimal.Round(wins.Count > 0 ? wins.Average(t => t.PnL) / losses.Average(t => Math.Abs(t.PnL)) : 0, 2)
                : 0,

            Expectancy = trades.Count > 0
                ? decimal.Round((decimal)wins.Count / trades.Count * (wins.Count > 0 ? wins.Average(t => t.PnL) : 0)
                    - (decimal)losses.Count / trades.Count * (losses.Count > 0 ? Math.Abs(losses.Average(t => t.PnL)) : 0), 2)
                : 0,

            AverageHoldingPeriod = holdings.Count > 0
                ? TimeSpan.FromTicks((long)holdings.Average(h => h.Ticks))
                : TimeSpan.Zero,
            LongestHoldingPeriod = holdings.Count > 0 ? holdings.Max() : TimeSpan.Zero,
            ShortestHoldingPeriod = holdings.Count > 0 ? holdings.Min() : TimeSpan.Zero,

            MaxDrawdownPct = decimal.Round((decimal)maxDrawdown, 4),
            RecoveryFactor = maxDrawdown > 0 ? decimal.Round(totalNet / (decimal)maxDrawdown, 2) : 0,

            TotalFees = decimal.Round(totalFees, 2),
            TotalSlippage = decimal.Round(totalSlippage, 2),
            // Trade.PnL 已扣手续费，滑点已含在成交价里 → 不再重复扣（修复原双重扣减）
            NetProfitAfterCosts = decimal.Round(totalNet, 2),

            ActiveDays = activeDays,
            TradesPerDay = activeDays > 0 ? Math.Round((double)trades.Count / activeDays, 2) : 0,

            MonthlyBreakdown = monthly,
        };
    }

    private static (int Wins, int Losses) CalculateConsecutiveStreaks(IReadOnlyList<Trade> trades)
    {
        int maxWins = 0, maxLosses = 0;
        int curWins = 0, curLosses = 0;

        // 按出场时间排序
        var sorted = trades.OrderBy(t => t.ExitTime).ToList();

        foreach (var trade in sorted)
        {
            if (trade.IsWin)
            {
                curWins++;
                curLosses = 0;
                if (curWins > maxWins) maxWins = curWins;
            }
            else
            {
                curLosses++;
                curWins = 0;
                if (curLosses > maxLosses) maxLosses = curLosses;
            }
        }

        return (maxWins, maxLosses);
    }
}

/// <summary>月度统计快照</summary>
public class MonthlyStats
{
    public int Trades { get; init; }
    public int Wins { get; init; }
    public decimal NetPnL { get; init; }
    public decimal Fees { get; init; }
    public decimal WinRate => Trades > 0 ? decimal.Round((decimal)Wins / Trades, 4) : 0;
}
