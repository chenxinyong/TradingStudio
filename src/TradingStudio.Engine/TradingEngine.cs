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
    private readonly FeedbackMonitor _feedback;
    private readonly EngineOptions _options;
    private readonly FutureRegistry _registry;

    public TradingEngine(
        IDataFeed dataFeed,
        IExecutionHandler execution,
        PortfolioManager portfolio,
        IndicatorManager indicators,
        StrategyContainer strategies,
        RiskController risk,
        FeedbackMonitor feedback,
        EngineOptions options,
        FutureRegistry registry)
    {
        _dataFeed = dataFeed;
        _execution = execution;
        _portfolio = portfolio;
        _indicators = indicators;
        _strategies = strategies;
        _risk = risk;
        _feedback = feedback;
        _options = options;
        _registry = registry;
    }

    /// <summary>运行引擎，返回完整报告。</summary>
    public async Task<EngineReport> RunAsync(CancellationToken ct = default)
    {
        _dataFeed.Initialize(_options.StartTime, _options.EndTime, _options.Instruments);

        // 实盘模式
        if (_execution is ExecutionHandler exec) exec.IsLive = _options.IsLive;

        // 1. 初始化策略
        var equityCurve = new List<(DateTimeOffset Time, decimal Equity)>();
        var globalTrades = new List<Trade>();
        var tradesLock = new object();

        var barHistories = new Dictionary<string, List<Bar>>();
        foreach (var config in _options.StrategyConfigs)
        {
            var strategy = StrategyFactory.Create(config);
            _portfolio.CreateSubPortfolio(config.StrategyId, config.AllocatedCapital);
            var barHistory = new List<Bar>();
            barHistories[config.StrategyId] = barHistory;

            // 预加载历史 Bar：回测模式加载全部，实盘按 WarmupDays 加载
            if (_dataFeed is Data.Engine.HistoricalBarFeed barFeed)
            {
                var loadStart = _options.IsLive
                    ? _options.StartTime.AddDays(-_options.WarmupDays)
                    : _options.StartTime;
                var loadEnd = _options.IsLive ? _options.StartTime : _options.EndTime;

                foreach (var inst in config.Instruments)
                {
                    await barFeed.LoadBars(inst, loadStart, loadEnd);
                    var loaded = barFeed.GetWarmupBars(inst);
                    barHistory.AddRange(loaded);
                }
                barHistory.Sort((a, b) => a.BarTime.CompareTo(b.BarTime));
            }

            var ctx = new EngineStrategyContext(
                config.StrategyId, _execution, _portfolio, _indicators,
                _registry, config.Instruments, barHistory);
            strategy.Initialize(ctx);

            // 预热：喂入历史 Bar 到策略（Warmup 模式，策略只更新状态不产生信号）
            if (barHistory.Count > 0)
            {
                ((EngineStrategyContext)ctx).IsWarmup = true;
                foreach (var bar in barHistory)
                    strategy.OnBar(bar);
                ((EngineStrategyContext)ctx).IsWarmup = false;
            }

            _strategies.Register(strategy, config, ctx);
            Console.WriteLine($"[Engine] Strategy '{config.StrategyId}' ({strategy.Name}) initialized (history={barHistory.Count} bars)");
        }

        // 2. 主循环
        Bar? prevBar = null;
        var firstBar = true;

        // 实盘模式：启动 CTP 成交回报消费
        var fillChannel = (_execution is ExecutionHandler exec2) ? exec2.FillChannel : null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fillReadTask = fillChannel != null
            ? Task.Run(async () =>
            {
                var reader = fillChannel.Reader;
                while (await reader.WaitToReadAsync(cts.Token))
                {
                    while (reader.TryRead(out var fill))
                    {
                        var trade = _portfolio.ProcessFill(fill, _registry);
                        lock (tradesLock) { if (trade != null) globalTrades.Add(trade); }
                        _strategies.DispatchOrderEvent(fill);
                    }
                }
            }, ct)
            : Task.CompletedTask;

        await foreach (var evt in _dataFeed.StreamAsync(ct))
        {
            switch (evt)
            {
                case TickEvent tickEvt:
                {
                    var inst = _registry.Resolve(tickEvt.InstrumentId);
                    if (inst == null) break;

                    // ① Tick 撮合 — 处理已有订单（限价/止损可能被触发）
                    var tickFills = _execution.ProcessTick(tickEvt.Tick, tickEvt.InstrumentId, inst);
                    foreach (var fill in tickFills)
                    {
                        var trade = _portfolio.ProcessFill(fill, _registry);
                        if (trade != null) globalTrades.Add(trade);
                        _feedback.RecordFill(fill, fill.StrategyId);
                        if (trade != null) _feedback.RecordTrade(trade, fill.StrategyId);
                        _strategies.DispatchOrderEvent(fill);
                    }

                    // ② 策略 OnTick
                    _strategies.DispatchTick(tickEvt);

                    // ③ 同 Tick 撮合新下的市价单
                    if (_options.TickFillDelay == 0 && _execution.ActiveOrders.Count > 0)
                    {
                        var newFills = _execution.ProcessTick(tickEvt.Tick, tickEvt.InstrumentId, inst);
                        foreach (var fill in newFills)
                        {
                            var trade = _portfolio.ProcessFill(fill, _registry);
                            if (trade != null) globalTrades.Add(trade);
                            _feedback.RecordFill(fill, fill.StrategyId);
                            if (trade != null) _feedback.RecordTrade(trade, fill.StrategyId);
                            _strategies.DispatchOrderEvent(fill);
                        }
                    }
                    break;
                }

                case BarEvent barEvt:
                {
                    var bar = barEvt.Bar;
                    var inst = _registry.Resolve(bar.InstrumentId);

                    // 按市价更新持仓未实现盈亏
                    if (inst != null) _portfolio.UpdateMarketPrice(bar, inst);

                    foreach (var slot in _strategies.AllSlots)
                        ((EngineStrategyContext)slot.Context).SetCurrentTime(barEvt.Time);

                    // Bar 撮合 — 处理上一轮 OnBar 中下的订单（前向偏差防护）
                    if (!firstBar && prevBar != null && inst != null)
                    {
                        var barFills = _execution.ProcessBar(bar, inst);
                        foreach (var fill in barFills)
                        {
                            var trade = _portfolio.ProcessFill(fill, _registry);
                            if (trade != null) globalTrades.Add(trade);
                            _feedback.RecordFill(fill, fill.StrategyId);
                            if (trade != null) _feedback.RecordTrade(trade, fill.StrategyId);
                            _strategies.DispatchOrderEvent(fill);
                        }
                    }

                    // 更新指标 → 策略 OnBar
                    _indicators.Feed(bar);
                    _strategies.DispatchBar(barEvt);

                    // 追加到策略历史（供 GetBarHistory / GetRecentBars 查询）
                    foreach (var history in barHistories.Values)
                        history.Add(bar);

                    // 反馈采样 + 告警
                    _feedback.SamplePortfolio(_portfolio);
                    var alerts = _feedback.CheckAlerts();
                    if (alerts.Count > 0) _strategies.DispatchAlert(alerts);

                    // 权益采样
                    equityCurve.Add((barEvt.Time, _portfolio.Equity));

                    prevBar = bar;
                    firstBar = false;
                    break;
                }
            }
        }

        // 3. 结束
        _strategies.DispatchEndOfAlgorithm();

        if (_options.IsLive)
        {
            // 实盘模式：持续运行，此处不可达（StreamAsync 不结束）
            await fillReadTask;
            return new EngineReport();
        }

        // 等待 CTP 回调消费完成
        if (fillChannel != null)
        {
            fillChannel.Writer.Complete();
            cts.Cancel();
            try { await fillReadTask; } catch (OperationCanceledException) { }
        }

        // 回测模式：生成报告
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
            MonitorSummary = _feedback.ToSummary(),
            ConfigSnapshots = _options.StrategyConfigs.ToList(),
        };
    }
}
