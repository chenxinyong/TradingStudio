using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>
/// 主循环引擎 — 回测和实盘共用。
/// </summary>
public class TradingEngine
{
    private readonly IDataFeed _dataFeed;
    private readonly IExecutionHandler _execution;
    private readonly PortfolioManager _portfolio;
    private readonly IndicatorManager _indicators;
    private readonly StrategyContainer _strategies;
    private readonly RiskController _risk;
    private readonly EngineOptions _options;
    private readonly FutureRegistry _registry;

    public TradingEngine(
        IDataFeed dataFeed,
        IExecutionHandler execution,
        PortfolioManager portfolio,
        IndicatorManager indicators,
        StrategyContainer strategies,
        RiskController risk,
        EngineOptions options,
        FutureRegistry registry)
    {
        _dataFeed = dataFeed;
        _execution = execution;
        _portfolio = portfolio;
        _indicators = indicators;
        _strategies = strategies;
        _risk = risk;
        _options = options;
        _registry = registry;
    }

    /// <summary>运行引擎，返回完整报告。</summary>
    public async Task<EngineReport> RunAsync(CancellationToken ct = default)
    {
        _dataFeed.Initialize(_options.StartTime, _options.EndTime, _options.Instruments);

        // 1. 初始化策略
        var equityCurve = new List<(DateTimeOffset Time, decimal Equity)>();
        var globalTrades = new List<Trade>();

        foreach (var config in _options.StrategyConfigs)
        {
            var strategy = StrategyFactory.Create(config);
            _portfolio.CreateSubPortfolio(config.StrategyId, config.AllocatedCapital);
            var barHistory = new List<Bar>();
            var ctx = new EngineStrategyContext(
                config.StrategyId, _execution, _portfolio, _indicators,
                _registry, config.Instruments, barHistory);
            strategy.Initialize(ctx);
            _strategies.Register(strategy, config, ctx);
            Console.WriteLine($"[Engine] Strategy '{config.StrategyId}' ({strategy.Name}) initialized");
        }

        // 2. 主循环
        Bar? prevBar = null;
        var firstBar = true;

        await foreach (var evt in _dataFeed.StreamAsync(ct))
        {
            if (evt is not BarEvent barEvt) continue;

            var bar = barEvt.Bar;
            var inst = _registry.Resolve(bar.InstrumentId);

            // 更新策略上下文中的历史 Bar
            foreach (var slot in _strategies.AllSlots)
                ((EngineStrategyContext)slot.Context).SetCurrentTime(barEvt.Time);

            if (!firstBar && prevBar != null)
            {
                // ① 用当前 Bar 撮合上一轮 Bar 中提交的订单
                //    关键：策略在 prevBar.OnBar 中下单，此时用本 Bar 的价格成交
                //    市价单 → 本 Bar Open，限价/止损 → 检查本 Bar 区间
                if (inst != null)
                {
                    var fills = _execution.ProcessBar(bar, inst);
                    foreach (var fill in fills)
                    {
                        var trade = _portfolio.ProcessFill(fill, _registry);
                        if (trade != null) globalTrades.Add(trade);

                        // 通知策略
                        _strategies.DispatchOrderEvent(fill);
                    }
                }
            }

            // ② 更新指标（在策略 OnBar 之前）
            _indicators.Feed(bar);

            // ③ 策略 OnBar 回调
            _strategies.DispatchBar(barEvt);

            // ④ 权益采样
            equityCurve.Add((barEvt.Time, _portfolio.Equity));

            prevBar = bar;
            firstBar = false;
        }

        // 3. 结束
        _strategies.DispatchEndOfAlgorithm();

        // 4. 确保所有策略的最终持仓信息反映在报告中
        Console.WriteLine($"[Engine] Done. Trades={globalTrades.Count} FinalEquity={_portfolio.Equity:C}");

        return new EngineReport
        {
            FinalPortfolio = new PortfolioSnapshot
            {
                StartingCapital = _portfolio.StartingCapital,
                TotalEquity = _portfolio.Equity,
                Cash = _portfolio.Cash,
                MarginUsed = _portfolio.MarginUsed,
                TotalPnl = (double)_portfolio.TotalPnL,
            },
            TotalReturn = _portfolio.StartingCapital > 0
                ? (_portfolio.Equity - _portfolio.StartingCapital) / _portfolio.StartingCapital
                : 0,
            MaxDrawdown = equityCurve.Count > 0
                ? (decimal)Statistics.DrawdownCalculator.CalculateMaxDrawdown(equityCurve)
                : 0,
            StrategyReports = _strategies.AllSlots.Select(slot =>
                Statistics.PerformanceReport.Generate(
                    slot.Config.StrategyId,
                    _portfolio.GetSubPortfolio(slot.Config.StrategyId),
                    globalTrades.Where(t => t.StrategyId == slot.Config.StrategyId).ToList(),
                    equityCurve)).ToList(),
            MonitorSummary = new MonitorSummary(),
            ConfigSnapshots = _options.StrategyConfigs.ToList(),
        };
    }
}
