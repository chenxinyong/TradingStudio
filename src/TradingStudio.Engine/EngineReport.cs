using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>引擎级报告 — 含各策略子报告 + 监控摘要。
/// 报告由 TradingEngine.RunAsync 内联构建，此类的 Generate 方法已废弃。</summary>
public class EngineReport
{
    public PortfolioSnapshot FinalPortfolio { get; init; } = new();
    public decimal TotalReturn { get; init; }
    public decimal MaxDrawdown { get; init; }
    public IReadOnlyList<Statistics.PerformanceReport> StrategyReports { get; init; } = [];
    public MonitorSummary MonitorSummary { get; init; } = new();
    public IReadOnlyList<StrategyConfig> ConfigSnapshots { get; init; } = [];
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
