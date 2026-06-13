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

public enum AlertType
{
    HighSlippage, HighRejectRate, ConsecutiveLosses, AbnormalFrequency,
    DrawdownWarning, ConnectionLost, TickDelay, BarMissing
}

public enum AlertSeverity { Info, Warning, Critical }
