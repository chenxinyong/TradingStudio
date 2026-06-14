using TradingStudio.Core.Engine;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine;

/// <summary>
/// 反馈监控中心 — 四维观测：执行质量 / 策略健康 / 风险状态 / 系统健康。
/// RiskController 是闸门（阻断），FeedbackMonitor 是仪表盘（观测）。
/// </summary>
public class FeedbackMonitor
{
    private readonly Dictionary<string, StrategyStats> _stats = new();
    private readonly List<MonitorAlert> _alerts = new();
    private readonly FeedbackThresholds _thresholds;

    // 系统健康
    public DateTime? LastQuoteTime { get; set; }
    public DateTime? LastBarTime { get; set; }
    public int ReconnectCount { get; set; }

    public IReadOnlyList<MonitorAlert> RecentAlerts => _alerts;

    public FeedbackMonitor(FeedbackThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? new FeedbackThresholds();
    }

    // ═══════════════════════════════════════════
    // 事件记录
    // ═══════════════════════════════════════════

    /// <summary>每笔成交 / 拒绝 / 取消后调用</summary>
    public void RecordFill(OrderEvent fill, string strategyId)
    {
        var s = GetOrCreate(strategyId);

        switch (fill.Type)
        {
            case OrderEventType.Filled or OrderEventType.PartiallyFilled:
                s.TotalFills++;
                s.TotalSlippage += (double)fill.Slippage;
                break;
            case OrderEventType.Rejected:
                s.TotalRejects++;
                break;
            case OrderEventType.Cancelled:
                s.TotalCancels++;
                break;
        }
    }

    /// <summary>每笔平仓交易后调用</summary>
    public void RecordTrade(Trade trade, string strategyId)
    {
        var s = GetOrCreate(strategyId);
        s.TotalTrades++;
        s.TotalPnl += (double)trade.PnL;

        if (trade.IsWin)
        {
            s.Wins++;
            s.ConsecutiveLosses = 0;
        }
        else
        {
            s.Losses++;
            s.ConsecutiveLosses++;
            if (s.ConsecutiveLosses > s.MaxConsecutiveLosses)
                s.MaxConsecutiveLosses = s.ConsecutiveLosses;
        }

        s.RecentTrades.Enqueue(trade);
        if (s.RecentTrades.Count > _thresholds.RollingWindowSize)
            s.RecentTrades.Dequeue();

        s.TradeTimestamps.Enqueue(DateTime.UtcNow);
        if (s.TradeTimestamps.Count > _thresholds.RollingWindowSize)
            s.TradeTimestamps.Dequeue();
    }

    /// <summary>每个时间步采样权益（Bar 闭合时）</summary>
    public void SamplePortfolio(IPortfolioState portfolio)
    {
        foreach (var sub in portfolio.SubPortfolios)
        {
            var s = GetOrCreate(sub.StrategyId);
            s.PeakEquity = Math.Max(s.PeakEquity, (double)sub.Equity);
            s.CurrentEquity = (double)sub.Equity;
        }
    }

    // ═══════════════════════════════════════════
    // 告警检查
    // ═══════════════════════════════════════════

    public IReadOnlyList<MonitorAlert> CheckAlerts()
    {
        _alerts.Clear();
        var now = DateTimeOffset.UtcNow;

        foreach (var (strategyId, s) in _stats)
        {
            // 连续亏损
            if (s.ConsecutiveLosses >= _thresholds.MaxConsecutiveLosses)
                _alerts.Add(new MonitorAlert
                {
                    Type = AlertType.ConsecutiveLosses,
                    StrategyId = strategyId,
                    Message = $"连续亏损 {s.ConsecutiveLosses} 笔",
                    Severity = AlertSeverity.Warning,
                    Timestamp = now,
                });

            // 高滑点
            var avgSlippage = s.TotalFills > 0 ? s.TotalSlippage / s.TotalFills : 0;
            if (avgSlippage > _thresholds.MaxAvgSlippage)
                _alerts.Add(new MonitorAlert
                {
                    Type = AlertType.HighSlippage,
                    StrategyId = strategyId,
                    Message = $"平均滑点 {avgSlippage:F2} 超限 ({_thresholds.MaxAvgSlippage})",
                    Severity = AlertSeverity.Warning,
                    Timestamp = now,
                });

            // 高拒绝率
            var totalOrders = s.TotalFills + s.TotalRejects + s.TotalCancels;
            var rejectRate = totalOrders > 0 ? (double)s.TotalRejects / totalOrders : 0;
            if (rejectRate > _thresholds.MaxRejectRate)
                _alerts.Add(new MonitorAlert
                {
                    Type = AlertType.HighRejectRate,
                    StrategyId = strategyId,
                    Message = $"拒绝率 {rejectRate:P1} 超限 ({_thresholds.MaxRejectRate:P0})",
                    Severity = AlertSeverity.Critical,
                    Timestamp = now,
                });

            // 回撤告警
            if (s.PeakEquity > 0)
            {
                var drawdown = (s.PeakEquity - s.CurrentEquity) / s.PeakEquity;
                if (drawdown > _thresholds.MaxDrawdownPct)
                    _alerts.Add(new MonitorAlert
                    {
                        Type = AlertType.DrawdownWarning,
                        StrategyId = strategyId,
                        Message = $"回撤 {drawdown:P1} 超限 ({_thresholds.MaxDrawdownPct:P0})",
                        Severity = AlertSeverity.Warning,
                        Timestamp = now,
                    });
            }

            // 异常交易频率
            if (s.TradeTimestamps.Count >= 10)
            {
                var elapsed = (now - s.TradeTimestamps.Peek()).TotalMinutes;
                var freq = elapsed > 0 ? s.TradeTimestamps.Count / elapsed : 0;
                if (freq > _thresholds.MaxTradesPerMinute)
                    _alerts.Add(new MonitorAlert
                    {
                        Type = AlertType.AbnormalFrequency,
                        StrategyId = strategyId,
                        Message = $"交易频率 {freq:F1}/min 异常 ({_thresholds.MaxTradesPerMinute}/min)",
                        Severity = AlertSeverity.Warning,
                        Timestamp = now,
                    });
            }
        }

        return _alerts;
    }

    // ═══════════════════════════════════════════
    // 汇总
    // ═══════════════════════════════════════════

    public MonitorSummary ToSummary()
    {
        double totalSlippage = 0;
        int totalFills = 0, maxConsecutiveLosses = 0;

        foreach (var s in _stats.Values)
        {
            totalSlippage += s.TotalSlippage;
            totalFills += s.TotalFills;
            if (s.MaxConsecutiveLosses > maxConsecutiveLosses)
                maxConsecutiveLosses = s.MaxConsecutiveLosses;
        }

        return new MonitorSummary
        {
            TotalSlippage = totalSlippage,
            AlertCount = _alerts.Count,
            MaxConsecutiveLosses = maxConsecutiveLosses,
        };
    }

    public StrategyStats GetStats(string strategyId) =>
        _stats.TryGetValue(strategyId, out var s) ? s : new StrategyStats();

    private StrategyStats GetOrCreate(string strategyId)
    {
        if (!_stats.TryGetValue(strategyId, out var s))
            _stats[strategyId] = s = new StrategyStats();
        return s;
    }
}

/// <summary>告警阈值配置</summary>
public class FeedbackThresholds
{
    public int MaxConsecutiveLosses { get; init; } = 5;
    public double MaxAvgSlippage { get; init; } = 3.0;
    public double MaxRejectRate { get; init; } = 0.1;
    public double MaxDrawdownPct { get; init; } = 0.15;
    public double MaxTradesPerMinute { get; init; } = 10;
    public int RollingWindowSize { get; init; } = 100;
}

/// <summary>单策略统计</summary>
public class StrategyStats
{
    // 执行质量
    public int TotalFills { get; set; }
    public int TotalRejects { get; set; }
    public int TotalCancels { get; set; }
    public double TotalSlippage { get; set; }

    // 策略健康
    public int TotalTrades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int ConsecutiveLosses { get; set; }
    public int MaxConsecutiveLosses { get; set; }
    public double TotalPnl { get; set; }
    public double WinRate => TotalTrades > 0 ? (double)Wins / TotalTrades : 0;

    // 滚动窗口
    public Queue<Trade> RecentTrades { get; } = new();
    public Queue<DateTime> TradeTimestamps { get; } = new();

    // 风险状态
    public double PeakEquity { get; set; }
    public double CurrentEquity { get; set; }
    public double Drawdown => PeakEquity > 0 ? (PeakEquity - CurrentEquity) / PeakEquity : 0;

    // 滚动指标
    public double RollingWinRate
    {
        get
        {
            if (RecentTrades.Count == 0) return 0;
            return (double)RecentTrades.Count(t => t.IsWin) / RecentTrades.Count;
        }
    }

    public double RollingProfitFactor
    {
        get
        {
            var wins = RecentTrades.Where(t => t.PnL > 0).Sum(t => (double)t.PnL);
            var losses = RecentTrades.Where(t => t.PnL <= 0).Sum(t => Math.Abs((double)t.PnL));
            return losses > 0 ? wins / losses : 0;
        }
    }
}

/// <summary>监控汇总（并入 EngineReport）</summary>
public record MonitorSummary
{
    public double TotalSlippage { get; init; }
    public int AlertCount { get; init; }
    public int MaxConsecutiveLosses { get; init; }
}
