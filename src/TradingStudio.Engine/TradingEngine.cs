using Microsoft.Extensions.Logging;
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
    private readonly TickSnapshot _tickSnapshot;
    private readonly EngineOptions _options;
    private readonly FutureRegistry _registry;
    private readonly ILogger _log;

    public TradingEngine(
        IDataFeed dataFeed,
        IExecutionHandler execution,
        PortfolioManager portfolio,
        IndicatorManager indicators,
        StrategyContainer strategies,
        RiskController risk,
        FeedbackMonitor feedback,
        TickSnapshot tickSnapshot,
        EngineOptions options,
        FutureRegistry registry,
        ILogger<TradingEngine>? logger = null)
    {
        _dataFeed = dataFeed;
        _execution = execution;
        _portfolio = portfolio;
        _indicators = indicators;
        _strategies = strategies;
        _risk = risk;
        _feedback = feedback;
        _tickSnapshot = tickSnapshot;
        _options = options;
        _registry = registry;
        _log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TradingEngine>.Instance;
    }

    /// <summary>运行引擎，返回完整报告。</summary>
    public async Task<EngineReport> RunAsync(CancellationToken ct = default)
    {
        _dataFeed.Initialize(_options.StartTime, _options.EndTime, _options.Instruments);

        // 实盘模式
        if (_execution is ExecutionHandler exec) exec.IsLive = _options.IsLive;

        // 1. 初始化策略
        var equityCurve = new List<(DateTimeOffset Time, decimal Equity)>();
        var strategyEquityCurves = new Dictionary<string, List<(DateTimeOffset, decimal)>>();
        var globalTrades = new List<Trade>();
        var tradesLock = new object();

        var barHistories = new Dictionary<string, List<Bar>>();                       // key=StrategyId
        var strategyInstruments = new Dictionary<string, HashSet<string>>();          // 策略→品种映射

        // 预热去重：多策略共享品种时只加载一次
        var allWarmupInstruments = _options.StrategyConfigs
            .SelectMany(c => c.Instruments).Distinct().ToList();
        var warmupCache = new Dictionary<string, List<Bar>>();

        foreach (var config in _options.StrategyConfigs)
        {
            var strategy = StrategyFactory.Create(config);
            _portfolio.CreateSubPortfolio(config.StrategyId, config.AllocatedCapital);
            var barHistory = new List<Bar>();
            barHistories[config.StrategyId] = barHistory;
            strategyInstruments[config.StrategyId] = new HashSet<string>(config.Instruments);

            if (_dataFeed is Data.Engine.HistoricalBarFeed barFeed)
            {
                var loadStart = _options.StartTime.AddDays(-Math.Max(_options.WarmupDays, 1));
                var loadEnd = _options.StartTime;  // 预热仅用回测期之前的数据，避免窗口污染

                foreach (var inst in config.Instruments)
                {
                    if (!warmupCache.TryGetValue(inst, out var loaded))
                    {
                        await barFeed.LoadBars(inst, loadStart, loadEnd);
                        loaded = barFeed.GetWarmupBars(inst).ToList();
                        warmupCache[inst] = loaded;
                    }
                    barHistory.AddRange(loaded);
                }
                barHistory.Sort((a, b) => a.BarTime.CompareTo(b.BarTime));
            }
            else if (_options.WarmupStore != null && _options.WarmupDays > 0)
            {
                // 实盘模式：从历史库加载最近 N 天 Bar 预热指标
                var loadStart = _options.StartTime.AddDays(-Math.Max(_options.WarmupDays * 2, 10));
                var loadEnd = _options.StartTime.AddDays(1);

                _log.LogInformation("[Warmup] Loading {Days}d from {Start:yyyy-MM-dd} to {End:yyyy-MM-dd}",
                    _options.WarmupDays, loadStart, loadEnd);
                foreach (var inst in config.Instruments)
                {
                    if (!warmupCache.TryGetValue(inst, out var loaded))
                    {
                        try
                        {
                            var bars = await _options.WarmupStore.QueryBarsAsync(inst, loadStart, loadEnd, "bars_1min");
                            if (bars.Count == 0)
                                bars = await _options.WarmupStore.QueryBarsAsync(inst, loadStart, loadEnd, $"bars_{inst}_1min");
                            loaded = bars.ToList();
                            _log.LogInformation("[Warmup] {Inst}: {Count} bars loaded", inst, loaded.Count);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "[Warmup] {Inst}: FAILED", inst);
                            loaded = new List<Bar>();
                        }
                        warmupCache[inst] = loaded;
                    }
                    barHistory.AddRange(loaded);
                }
                barHistory.Sort((a, b) => a.BarTime.CompareTo(b.BarTime));
            }

            var ctx = new EngineStrategyContext(
                config.StrategyId, _execution, _portfolio, _indicators,
                _registry, config.Instruments, barHistory, _log);
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
            if (_execution is ExecutionHandler eh)
                eh.SetStrategyPriority(config.StrategyId, config.Priority);
            _log.LogInformation("[Engine] Strategy '{Id}' ({Name}) initialized (warmup={Count} bars)",
                config.StrategyId, strategy.Name, barHistory.Count);
        }

        // 2. 主循环
        Bar? prevBar = null;
        var firstBar = true;
        DateOnly? lastTradingDay = null;   // 每日盯市结算：跟踪交易日切换

        // 实盘模式：启动 CTP 成交回报消费
        var fillChannel = (_execution is ExecutionHandler exec2) ? exec2.FillChannel : null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fillReadTask = fillChannel != null
            ? Task.Run(async () =>
            {
                var reader = fillChannel.Reader;
                var outboxWriter = (_execution is ExecutionHandler exec2) ? exec2.OrderOutbox.Writer : null;
                while (await reader.WaitToReadAsync(cts.Token))
                {
                    while (reader.TryRead(out var fill))
                    {
                        var trade = _portfolio.ProcessFill(fill, _registry);
                        lock (tradesLock) { if (trade != null) globalTrades.Add(trade); }
                        _strategies.DispatchOrderEvent(fill);
                        outboxWriter?.TryWrite(fill);  // 通知 SignalR 推送端
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
                    // 集合竞价过滤：跳过非连续交易时段的噪音 Tick
                    if (_options.SkipAuction && tickEvt.Tick.IsAuction)
                        break;

                    var inst = _registry.Resolve(tickEvt.InstrumentId);
                    if (inst == null) break;

                    // 更新行情快照
                    _tickSnapshot.Update(tickEvt.InstrumentId, tickEvt.Tick, tickEvt.Time);

                    // ① Tick 撮合 — 处理已有订单（限价/止损可能被触发）
                    var tickFills = _execution.ProcessTick(tickEvt.Tick, tickEvt.InstrumentId, inst);
                    foreach (var fill in tickFills)
                    {
                        var trade = _portfolio.ProcessFill(fill, _registry);
                        lock (tradesLock) { if (trade != null) globalTrades.Add(trade); }
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
                            lock (tradesLock) { if (trade != null) globalTrades.Add(trade); }
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

                    // 每日无负债结算：交易日切换时，用上一交易日最后收盘价对全部持仓盯市结算。
                    // 必须在 UpdateMarketPrice 之前——此刻 pos.MarketPrice 仍是上一交易日的最后收盘价。
                    if (!_options.IsLive && lastTradingDay != null && bar.TradingDay != lastTradingDay)
                        _portfolio.SettleDaily(_registry);
                    lastTradingDay = bar.TradingDay;

                    // 按市价更新持仓未实现盈亏
                    if (inst != null) _portfolio.UpdateMarketPrice(bar, inst);

                    // 保证金强平（爆仓）：权益跌破占用保证金 → 全部持仓按当前价强制平仓
                    if (inst != null && !_options.IsLive)
                    {
                        foreach (var fill in _portfolio.CheckMarginCall(bar))
                        {
                            var liqTrade = _portfolio.ProcessFill(fill, _registry);
                            lock (tradesLock) { if (liqTrade != null) globalTrades.Add(liqTrade); }
                            _feedback.RecordFill(fill, fill.StrategyId);
                            if (liqTrade != null) _feedback.RecordTrade(liqTrade, fill.StrategyId);
                            _strategies.DispatchOrderEvent(fill);
                        }
                    }

                    // 交割月检查：到期前强制平仓（防止进入交割月）
                    if (inst != null && !_options.IsLive)
                        _portfolio.ForceCloseNearDelivery(bar, inst, _registry);

                    foreach (var slot in _strategies.AllSlots)
                        ((EngineStrategyContext)slot.Context).SetCurrentTime(barEvt.Time);

                    // Bar 撮合 — 处理上一轮 OnBar 中下的订单（前向偏差防护）
                    if (!firstBar && prevBar != null && inst != null)
                    {
                        var barFills = _execution.ProcessBar(bar, inst);
                        foreach (var fill in barFills)
                        {
                            var trade = _portfolio.ProcessFill(fill, _registry);
                            lock (tradesLock) { if (trade != null) globalTrades.Add(trade); }
                            _feedback.RecordFill(fill, fill.StrategyId);
                            if (trade != null) _feedback.RecordTrade(trade, fill.StrategyId);
                            _strategies.DispatchOrderEvent(fill);
                        }
                    }

                    // 更新指标 → 策略 OnBar
                    _indicators.Feed(bar);
                    _strategies.DispatchBar(barEvt);

                    // 追加到策略历史（仅该策略订阅的品种）
                    foreach (var (strategyId, history) in barHistories)
                    {
                        if (strategyInstruments.TryGetValue(strategyId, out var insts)
                            && insts.Contains(bar.InstrumentId))
                            history.Add(bar);
                    }

                    // 反馈采样 + 告警
                    _feedback.SamplePortfolio(_portfolio);

                    // 周期性风控（三级风控之 Periodic 层）：回撤/敞口等趋势性检查，只告警不阻断
                    foreach (var w in _risk.CheckPeriodic(_portfolio))
                        _log.LogWarning("[Risk/Periodic] {Rule}: {Reason}", w.RuleName, w.Reason);

                    var alerts = _feedback.CheckAlerts();
                    if (alerts.Count > 0) _strategies.DispatchAlert(alerts);

                    // 权益采样（全局 + 按策略）
                    equityCurve.Add((barEvt.Time, _portfolio.Equity));
                    foreach (var slot in _strategies.AllSlots)
                    {
                        var sid = slot.Config.StrategyId;
                        if (!strategyEquityCurves.ContainsKey(sid))
                            strategyEquityCurves[sid] = new();
                        var subEquity = _portfolio.TryGetSubPortfolio(sid, out var sub)
                            ? sub!.Equity : 0;
                        strategyEquityCurves[sid].Add((barEvt.Time, subEquity));
                    }

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
        _log.LogInformation("[Engine] Done. Trades={TradeCount} FinalEquity={Equity:C}",
            globalTrades.Count, _portfolio.Equity);

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
                    strategyEquityCurves.TryGetValue(slot.Config.StrategyId, out var se)
                        ? se : equityCurve)).ToList(),
            MonitorSummary = _feedback.ToSummary(),
            ConfigSnapshots = _options.StrategyConfigs.ToList(),
        };
    }
}
