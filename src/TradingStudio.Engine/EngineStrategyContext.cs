using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine;

/// <summary>
/// 策略上下文的引擎层具体实现 — 策略与引擎之间的唯一触点。
/// 策略通过此对象查询行情、下单、查仓位，看不到引擎内部。
/// </summary>
internal class EngineStrategyContext : StrategyContext
{
    private readonly IExecutionHandler _execution;
    private readonly PortfolioManager _portfolio;
    private readonly IndicatorManager _indicators;
    private readonly FutureRegistry _registry;
    private readonly IReadOnlyList<string> _instruments;
    private readonly List<Bar> _barHistory;

    private DateTimeOffset _currentTime;
    public override DateTimeOffset CurrentTime => _currentTime;
    public override IReadOnlyList<string> SubscribedInstruments => _instruments;

    /// <summary>预热模式：策略应只更新内部状态，不产生交易信号</summary>
    public bool IsWarmup { get; set; }

    public EngineStrategyContext(
        string strategyId,
        IExecutionHandler execution,
        PortfolioManager portfolio,
        IndicatorManager indicators,
        FutureRegistry registry,
        IReadOnlyList<string> instruments,
        List<Bar> barHistory)
        : base(strategyId)
    {
        _execution = execution;
        _portfolio = portfolio;
        _indicators = indicators;
        _registry = registry;
        _instruments = instruments;
        _barHistory = barHistory;
    }

    public void SetCurrentTime(DateTimeOffset time) => _currentTime = time;

    // ═══ 行情 ═══
    public override IReadOnlyList<Bar> GetBarHistory(string instrumentId) =>
        _barHistory.Where(b => b.InstrumentId == instrumentId).ToList();

    public override IReadOnlyList<Bar> GetRecentBars(string instrumentId, int count)
    {
        var bars = _barHistory.Where(b => b.InstrumentId == instrumentId).ToList();
        return bars.Skip(Math.Max(0, bars.Count - count)).ToList();
    }

    // ═══ 指标 ═══
    public override T RegisterIndicator<T>(string instrumentId, T indicator, string tag = "") =>
        _indicators.Register(instrumentId, indicator, StrategyId, tag);

    public override double GetIndicatorValue(string instrumentId, string indicatorName, string tag = "") =>
        _indicators.GetValue(instrumentId, indicatorName, tag);

    public override T? GetIndicator<T>(string instrumentId, string tag = "") where T : class =>
        _indicators.Get<T>(instrumentId, tag);

    // ═══ 交易 ═══
    public override OrderTicket MarketBuy(string instrumentId, int quantity, string? tag = null) =>
        _execution.Submit(new Order
        {
            InstrumentId = instrumentId,
            Direction = OrderDirection.Buy,
            Type = OrderType.Market,
            Quantity = quantity,
            Tag = tag,
        }, StrategyId, _portfolio);

    public override OrderTicket MarketSell(string instrumentId, int quantity, string? tag = null) =>
        _execution.Submit(new Order
        {
            InstrumentId = instrumentId,
            Direction = OrderDirection.Sell,
            Type = OrderType.Market,
            Quantity = quantity,
            Tag = tag,
        }, StrategyId);

    public override OrderTicket ClosePosition(string instrumentId)
    {
        var pos = _portfolio.GetPosition(instrumentId);
        if (pos == null || pos.Quantity == 0)
            throw new InvalidOperationException($"No position to close: {instrumentId}");
        return pos.Quantity > 0
            ? MarketSell(instrumentId, pos.Quantity, "平多")
            : MarketBuy(instrumentId, -pos.Quantity, "平空");
    }

    public override OrderTicket LimitBuy(string instrumentId, int quantity, decimal limitPrice) =>
        _execution.Submit(new Order
        {
            InstrumentId = instrumentId, Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = quantity, LimitPrice = limitPrice,
        }, StrategyId);

    public override OrderTicket LimitSell(string instrumentId, int quantity, decimal limitPrice) =>
        _execution.Submit(new Order
        {
            InstrumentId = instrumentId, Direction = OrderDirection.Sell,
            Type = OrderType.Limit, Quantity = quantity, LimitPrice = limitPrice,
        }, StrategyId);

    public override OrderTicket StopBuy(string instrumentId, int quantity, decimal stopPrice) =>
        _execution.Submit(new Order
        {
            InstrumentId = instrumentId, Direction = OrderDirection.Buy,
            Type = OrderType.Stop, Quantity = quantity, StopPrice = stopPrice,
        }, StrategyId);

    public override OrderTicket StopSell(string instrumentId, int quantity, decimal stopPrice) =>
        _execution.Submit(new Order
        {
            InstrumentId = instrumentId, Direction = OrderDirection.Sell,
            Type = OrderType.Stop, Quantity = quantity, StopPrice = stopPrice,
        }, StrategyId);

    // ═══ 仓位 ═══
    public override Position? GetPosition(string instrumentId) =>
        _portfolio.GetPosition(instrumentId);

    public override IReadOnlyList<Position> Positions => _portfolio.AllPositions;
    public override decimal Equity => _portfolio.Equity;
    public override decimal AvailableCash => _portfolio.Cash;

    // ═══ 品种 ═══
    public override Future GetFuture(string instrumentId) =>
        _registry.Resolve(instrumentId)!;

    // ═══ 日志 ═══
    public override void Log(string message) =>
        Console.WriteLine($"[{StrategyId}] {message}");

    public override void LogWarning(string message) =>
        Console.WriteLine($"[{StrategyId}] ⚠ {message}");

    public override void LogError(string message) =>
        Console.WriteLine($"[{StrategyId}] ✗ {message}");
}
