namespace TradingStudio.Core.Risk;

/// <summary>
/// 日内/日间风险追踪器 — Ernie Chan Ch6.2: 风险管理。
///
/// Chan 的多层风险限制：
///   单笔风险 ≤ 权益的 1-2%
///   日风险   ≤ 权益的 3-5%    ← 日内触及即停止交易
///   月风险   ≤ 权益的 10-15%  ← 触及后暂停当月
///   最大回撤 ≤ 权益的 20-30%  ← 断路器，停止策略
///
/// 这些不是建议，是纪律。Chan: "大多数 quant 的失败不是因为策略差，
/// 而是因为没有严格遵守风险限制。"
/// </summary>
public class DailyRiskTracker
{
    private readonly RiskTrackerConfig _config;
    private readonly List<DailyPnL> _dailyHistory = new();
    private double _peakEquity;
    private int _consecutiveLossDays;

    public DailyRiskTracker(RiskTrackerConfig? config = null)
    {
        _config = config ?? new RiskTrackerConfig();
    }

    /// <summary>当前状态</summary>
    public RiskState State { get; private set; } = RiskState.Normal;

    /// <summary>当日累计盈亏（相对于今日起始权益的比例）</summary>
    public double TodayPnlRatio { get; private set; }

    /// <summary>当月累计盈亏比例</summary>
    public double MonthPnlRatio { get; private set; }

    /// <summary>当前回撤比例</summary>
    public double CurrentDrawdown { get; private set; }

    /// <summary>连续亏损天数</summary>
    public int ConsecutiveLossDays => _consecutiveLossDays;

    /// <summary>高峰权益</summary>
    public double PeakEquity => _peakEquity;

    // ═══════════════════════════════════════════
    // 每日调用
    // ═══════════════════════════════════════════

    /// <summary>
    /// 记录一笔成交的盈亏，更新所有风险指标。
    /// 在每笔交易成交后调用。
    /// </summary>
    /// <returns>如果触发了风险限制，返回对应的 RiskEvent</returns>
    public RiskEvent? RecordTrade(double tradePnl, double currentEquity)
    {
        if (_peakEquity == 0)
            _peakEquity = currentEquity;

        // ── 更新当日盈亏 ──
        TodayPnlRatio += tradePnl / currentEquity;

        // ── 检查单日亏损限制 ──
        if (TodayPnlRatio < -_config.DailyLossLimit)
        {
            State = RiskState.DailyLimitHit;
            return new RiskEvent
            {
                Type = RiskEventType.DailyLossLimit,
                Message = $"日内亏损 {TodayPnlRatio:P1} 触及日止损线 {-_config.DailyLossLimit:P1}",
                CurrentEquity = currentEquity,
                PnlRatio = TodayPnlRatio,
                Action = "立即停止当日所有交易，明天重置",
            };
        }

        // ── 更新当月盈亏 ──
        MonthPnlRatio += tradePnl / currentEquity;
        if (MonthPnlRatio < -_config.MonthlyLossLimit)
        {
            State = RiskState.MonthlyLimitHit;
            return new RiskEvent
            {
                Type = RiskEventType.MonthlyLossLimit,
                Message = $"当月亏损 {MonthPnlRatio:P1} 触及月止损线 {-_config.MonthlyLossLimit:P1}",
                CurrentEquity = currentEquity,
                PnlRatio = MonthPnlRatio,
                Action = "暂停当月所有交易，次月重置",
            };
        }

        // ── 更新回撤 ──
        if (currentEquity > _peakEquity)
            _peakEquity = currentEquity;
        CurrentDrawdown = _peakEquity > 0 ? (currentEquity - _peakEquity) / _peakEquity : 0;

        if (CurrentDrawdown < -_config.MaxDrawdownLimit)
        {
            State = RiskState.MaxDrawdownHit;
            return new RiskEvent
            {
                Type = RiskEventType.MaxDrawdown,
                Message = $"回撤 {CurrentDrawdown:P1} 触及最大回撤线 {-_config.MaxDrawdownLimit:P1}",
                CurrentEquity = currentEquity,
                PnlRatio = CurrentDrawdown,
                Action = "停止策略，重新评估后再决定是否恢复",
            };
        }

        return null; // 正常
    }

    /// <summary>
    /// 每日收盘调用 — 记录日终状态，追踪连续亏损天数。
    /// </summary>
    public DailyPnL CloseDay(double dayStartEquity, double dayEndEquity)
    {
        var dayPnl = dayEndEquity - dayStartEquity;
        var dayPnlRatio = dayStartEquity > 0 ? dayPnl / dayStartEquity : 0;

        var record = new DailyPnL
        {
            Date = DateTime.Today,
            StartEquity = dayStartEquity,
            EndEquity = dayEndEquity,
            PnL = dayPnl,
            PnLRatio = dayPnlRatio,
            PeakEquity = _peakEquity,
            Drawdown = CurrentDrawdown,
        };

        _dailyHistory.Add(record);

        // 更新连续亏损天数
        if (dayPnl < 0)
            _consecutiveLossDays++;
        else
            _consecutiveLossDays = 0;

        // 检查连续亏损
        if (_consecutiveLossDays >= _config.MaxConsecutiveLossDays)
        {
            State = RiskState.ConsecutiveLosses;
            record.RiskEvent = new RiskEvent
            {
                Type = RiskEventType.ConsecutiveLosses,
                Message = $"连续亏损 {_consecutiveLossDays} 天，触及限制",
                Action = $"暂停交易 1-2 天，检查策略是否适应当前市场。Chan: '连续亏损超过 5 天就该检查代码或市场状态。'",
            };
        }

        // 重置日内计数器
        TodayPnlRatio = 0;

        return record;
    }

    /// <summary>
    /// 月初调用 — 重置月度累计。
    /// </summary>
    public void ResetMonth()
    {
        MonthPnlRatio = 0;
        if (State == RiskState.MonthlyLimitHit)
            State = RiskState.Normal;
    }

    /// <summary>
    /// 检查新交易是否被当前风险状态允许。
    /// </summary>
    public bool CanTrade()
    {
        return State switch
        {
            RiskState.Normal => true,
            RiskState.DailyLimitHit => false,     // 日内禁止
            RiskState.MonthlyLimitHit => false,    // 当月禁止
            RiskState.MaxDrawdownHit => false,     // 禁止直到人工恢复
            RiskState.ConsecutiveLosses => false,  // 暂停
            _ => false,
        };
    }

    /// <summary>手动恢复交易（仅在确认问题已解决后）</summary>
    public void Resume()
    {
        if (State == RiskState.MaxDrawdownHit)
            return; // 最大回撤需要人工改配置
        State = RiskState.Normal;
    }

    /// <summary>获取日盈亏历史，用于分析</summary>
    public IReadOnlyList<DailyPnL> GetHistory() => _dailyHistory.AsReadOnly();

    /// <summary>计算滚动 Sharpe（最近 N 个交易日）</summary>
    public double RollingSharpe(int days = 60)
    {
        var recent = _dailyHistory.TakeLast(days).Select(d => d.PnLRatio).ToList();
        if (recent.Count < 10) return 0;
        var mean = recent.Average();
        var std = Math.Sqrt(recent.Average(r => Math.Pow(r - mean, 2)));
        return std > 0 ? mean / std * Math.Sqrt(252) : 0;
    }
}

/// <summary>风险追踪配置</summary>
public class RiskTrackerConfig
{
    /// <summary>单日亏损上限（占权益比例，默认 5%）</summary>
    public double DailyLossLimit { get; init; } = 0.05;

    /// <summary>单月亏损上限（占权益比例，默认 15%）</summary>
    public double MonthlyLossLimit { get; init; } = 0.15;

    /// <summary>最大回撤限制（占高峰权益比例，默认 25%）</summary>
    public double MaxDrawdownLimit { get; init; } = 0.25;

    /// <summary>最大连续亏损天数（默认 5 天）</summary>
    public int MaxConsecutiveLossDays { get; init; } = 5;

    /// <summary>滚动 Sharpe 告警线（默认 0.3）</summary>
    public double RollingSharpeWarning { get; init; } = 0.3;
}

/// <summary>风险状态</summary>
public enum RiskState
{
    Normal,
    DailyLimitHit,
    MonthlyLimitHit,
    MaxDrawdownHit,
    ConsecutiveLosses,
}

/// <summary>风险事件类型</summary>
public enum RiskEventType
{
    DailyLossLimit,
    MonthlyLossLimit,
    MaxDrawdown,
    ConsecutiveLosses,
}

/// <summary>风险事件</summary>
public class RiskEvent
{
    public RiskEventType Type { get; init; }
    public string Message { get; init; } = "";
    public double CurrentEquity { get; init; }
    public double PnlRatio { get; init; }
    public string Action { get; init; } = "";
}

/// <summary>日终记录</summary>
public class DailyPnL
{
    public DateTime Date { get; init; }
    public double StartEquity { get; init; }
    public double EndEquity { get; init; }
    public double PnL { get; init; }
    public double PnLRatio { get; init; }
    public double PeakEquity { get; init; }
    public double Drawdown { get; init; }
    public RiskEvent? RiskEvent { get; set; }
}
