using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>
/// 主循环引擎 — 回测和实盘共用。
/// Phase 2a: 单策略 Bar 回放。
/// </summary>
public class TradingEngine
{
    private readonly IDataFeed _dataFeed;
    private readonly IExecutionHandler _execution;
    private readonly PortfolioManager _portfolio;
    private readonly IndicatorManager _indicators;
    private readonly StrategyContainer _strategies;
    private readonly FeedbackMonitor _feedback;
    private readonly EngineOptions _options;
    private readonly FutureRegistry _registry;

    public TradingEngine(
        IDataFeed dataFeed,
        IExecutionHandler execution,
        PortfolioManager portfolio,
        IndicatorManager indicators,
        StrategyContainer strategies,
        FeedbackMonitor feedback,
        EngineOptions options,
        FutureRegistry registry)
    {
        _dataFeed = dataFeed;
        _execution = execution;
        _portfolio = portfolio;
        _indicators = indicators;
        _strategies = strategies;
        _feedback = feedback;
        _options = options;
        _registry = registry;
    }

    /// <summary>运行引擎，返回完整报告。</summary>
    public Task<EngineReport> RunAsync(CancellationToken ct = default)
    {
        // Phase 2a 实现
        throw new NotImplementedException("Phase 2a: 实现主循环");
    }
}
