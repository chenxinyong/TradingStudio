using TradingStudio.Core.Engine;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine;

/// <summary>
/// 反馈监控中心 — 观测层，不阻断，只记录和预警。
/// Phase 2a: 空桩（数据结构定义，统计逻辑 Phase 2b 实现）。
/// </summary>
public class FeedbackMonitor
{
    private readonly List<MonitorAlert> _alerts = new();

    public IReadOnlyList<MonitorAlert> RecentAlerts => _alerts;

    /// <summary>每笔成交后调用 — 记录执行质量</summary>
    public void RecordFill(OrderEvent fill, string strategyId)
    {
        // Phase 2b 实现
    }

    /// <summary>每笔平仓后调用 — 记录策略健康</summary>
    public void RecordTrade(Trade trade, string strategyId)
    {
        // Phase 2b 实现
    }

    /// <summary>每个时间步采样权益</summary>
    public void SamplePortfolio(IPortfolioState portfolio)
    {
        // Phase 2b 实现
    }

    /// <summary>检查是否触发告警</summary>
    public IReadOnlyList<MonitorAlert> CheckAlerts()
    {
        // Phase 2b 实现
        return [];
    }

    /// <summary>生成监控摘要，并入 PerformanceReport</summary>
    public MonitorSummary ToSummary() => new();
}

/// <summary>监控摘要</summary>
public record MonitorSummary
{
    public double TotalSlippage { get; init; }
    public int AlertCount { get; init; }
    public int MaxConsecutiveLosses { get; init; }
}
