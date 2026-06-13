using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>引擎级报告 — 含各策略子报告 + 监控摘要</summary>
public class EngineReport
{
    public PortfolioSnapshot FinalPortfolio { get; init; } = new();
    public decimal TotalReturn { get; init; }
    public decimal MaxDrawdown { get; init; }
    public IReadOnlyList<Statistics.PerformanceReport> StrategyReports { get; init; } = [];
    public MonitorSummary MonitorSummary { get; init; } = new();
    public IReadOnlyList<StrategyConfig> ConfigSnapshots { get; init; } = [];

    public static EngineReport Generate(
        PortfolioManager portfolio,
        StrategyContainer strategies,
        FeedbackMonitor feedback,
        List<(DateTimeOffset Time, decimal Equity)> equityCurve,
        List<Core.Engine.Trade> trades,
        EngineOptions options)
    {
        // Phase 2a 实现
        throw new NotImplementedException("Phase 2a: 实现报告生成");
    }
}

/// <summary>总账快照（供 API 用）</summary>
public record PortfolioSnapshot
{
    public decimal StartingCapital { get; init; }
    public decimal TotalEquity { get; init; }
    public decimal Cash { get; init; }
    public decimal MarginUsed { get; init; }
    public double TotalPnl { get; init; }
}
