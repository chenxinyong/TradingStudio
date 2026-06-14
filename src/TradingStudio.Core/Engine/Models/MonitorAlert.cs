namespace TradingStudio.Core.Engine;

/// <summary>反馈监控告警</summary>
public record MonitorAlert
{
    public AlertType Type { get; init; }
    public string StrategyId { get; init; } = "";
    public string Message { get; init; } = "";
    public AlertSeverity Severity { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// 告警类型枚举 — 可根据实际需求扩展，如高滑点、高拒单率、连续亏损、异常频率等。
/// </summary>
public enum AlertType
{
    HighSlippage, HighRejectRate, ConsecutiveLosses, AbnormalFrequency,
    DrawdownWarning, ConnectionLost, TickDelay, BarMissing
}

/// <summary>
/// 告警严重程度枚举 — 可根据实际需求定义不同级别的告警处理逻辑，如仅记录、发送通知、自动调整策略参数等。
/// </summary>
public enum AlertSeverity { Info, Warning, Critical }
