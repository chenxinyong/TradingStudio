using Microsoft.Extensions.Logging;
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
    private readonly ILogger _log;

    private DateTimeOffset _currentTime;
    public override DateTimeOffset CurrentTime => _currentTime;
    public override IReadOnlyList<string> SubscribedInstruments => _instruments;
    public override bool IsWarmup { get; set; }

    public EngineStrategyContext(
        string strategyId,
        IExecutionHandler execution,
        PortfolioManager portfolio,
        IndicatorManager indicators,
        FutureRegistry registry,
        IReadOnlyList<string> instruments,
        List<Bar> barHistory,
        ILogger logger)
        : base(strategyId)
    {
        _execution = execution;
        _portfolio = portfolio;
        _indicators = indicators;
        _registry = registry;
        _instruments = instruments;
        _barHistory = barHistory;
        _log = logger;
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
    public override OrderTicket MarketBuy(string instrumentId, int quantity, string? tag = null)
    {
        var warmup = IsWarmup;
        if (!warmup && GateOpen(instrumentId, OrderDirection.Buy, quantity, LastClose(instrumentId)) is { } g) return g;
        var ticket = warmup ? new OrderTicket { OrderId = 0, Status = OrderStatus.Rejected }
            : _execution.Submit(new Order
            {
                InstrumentId = instrumentId, Direction = OrderDirection.Buy,
                Type = OrderType.Market, Quantity = quantity, Tag = tag,
            }, StrategyId, _portfolio);
        if (!warmup)
            _log.LogDebug("[{Strategy}] MarketBuy {Inst} x{Qty} {Tag} → {Status}",
                StrategyId, instrumentId, quantity, tag ?? "", ticket.Status);
        return ticket;
    }

    public override OrderTicket MarketSell(string instrumentId, int quantity, string? tag = null)
    {
        var warmup = IsWarmup;
        if (!warmup && GateOpen(instrumentId, OrderDirection.Sell, quantity, LastClose(instrumentId)) is { } g) return g;
        var ticket = warmup ? new OrderTicket { OrderId = 0, Status = OrderStatus.Rejected }
            : _execution.Submit(new Order
            {
                InstrumentId = instrumentId, Direction = OrderDirection.Sell,
                Type = OrderType.Market, Quantity = quantity, Tag = tag,
            }, StrategyId, _portfolio);
        if (!warmup)
            _log.LogDebug("[{Strategy}] MarketSell {Inst} x{Qty} {Tag} → {Status}",
                StrategyId, instrumentId, quantity, tag ?? "", ticket.Status);
        return ticket;
    }

    public override OrderTicket ClosePosition(string instrumentId)
    {
        if (IsWarmup) return new OrderTicket { OrderId = 0, Status = OrderStatus.Rejected };
        var pos = _portfolio.GetPosition(instrumentId);
        if (pos == null || pos.Quantity == 0)
            throw new InvalidOperationException($"No position to close: {instrumentId}");
        var direction = pos.Quantity > 0 ? OrderDirection.Sell : OrderDirection.Buy;
        var quantity = Math.Abs(pos.Quantity);
        var ticket = _execution.Submit(new Order
        {
            InstrumentId = instrumentId, Direction = direction,
            Type = OrderType.Market, Quantity = quantity,
            Tag = pos.Quantity > 0 ? "平多" : "平空",
            IsCloseOrder = true,
        }, StrategyId, _portfolio);
        _log.LogInformation("[{Strategy}] ClosePosition {Inst} x{Qty} → {Status}",
            StrategyId, instrumentId, quantity, ticket.Status);
        return ticket;
    }

    public override OrderTicket LimitBuy(string instrumentId, int quantity, decimal limitPrice)
    {
        if (IsWarmup) return new OrderTicket { OrderId = 0, Status = OrderStatus.Rejected };
        if (GateOpen(instrumentId, OrderDirection.Buy, quantity, limitPrice) is { } g) return g;
        return _execution.Submit(new Order
        {
            InstrumentId = instrumentId, Direction = OrderDirection.Buy,
            Type = OrderType.Limit, Quantity = quantity, LimitPrice = limitPrice,
        }, StrategyId, _portfolio);
    }

    public override OrderTicket LimitSell(string instrumentId, int quantity, decimal limitPrice)
    {
        if (IsWarmup) return new OrderTicket { OrderId = 0, Status = OrderStatus.Rejected };
        if (GateOpen(instrumentId, OrderDirection.Sell, quantity, limitPrice) is { } g) return g;
        return _execution.Submit(new Order
        {
            InstrumentId = instrumentId, Direction = OrderDirection.Sell,
            Type = OrderType.Limit, Quantity = quantity, LimitPrice = limitPrice,
        }, StrategyId, _portfolio);
    }

    public override OrderTicket StopBuy(string instrumentId, int quantity, decimal stopPrice)
    {
        if (IsWarmup) return new OrderTicket { OrderId = 0, Status = OrderStatus.Rejected };
        if (GateOpen(instrumentId, OrderDirection.Buy, quantity, stopPrice) is { } g) return g;
        return _execution.Submit(new Order
        {
            InstrumentId = instrumentId, Direction = OrderDirection.Buy,
            Type = OrderType.Stop, Quantity = quantity, StopPrice = stopPrice,
        }, StrategyId, _portfolio);
    }

    public override OrderTicket StopSell(string instrumentId, int quantity, decimal stopPrice)
    {
        if (IsWarmup) return new OrderTicket { OrderId = 0, Status = OrderStatus.Rejected };
        if (GateOpen(instrumentId, OrderDirection.Sell, quantity, stopPrice) is { } g) return g;
        return _execution.Submit(new Order
        {
            InstrumentId = instrumentId, Direction = OrderDirection.Sell,
            Type = OrderType.Stop, Quantity = quantity, StopPrice = stopPrice,
        }, StrategyId, _portfolio);
    }

    // ═══ 购买力硬闸门 ═══
    // 下单前估算开仓所需保证金，可用现金不足则拒单（不改风控签名，策略同步拿到拒单）。
    private OrderTicket? GateOpen(string instrumentId, OrderDirection dir, int quantity, decimal estPrice)
    {
        if (CanAffordOpen(instrumentId, dir, quantity, estPrice)) return null;
        _log.LogWarning("[{Strategy}] {Dir} {Inst} x{Qty} 资金不足 → 购买力闸门拒单",
            StrategyId, dir, instrumentId, quantity);
        return new OrderTicket { OrderId = 0, Status = OrderStatus.Rejected };
    }

    private bool CanAffordOpen(string instrumentId, OrderDirection dir, int quantity, decimal estPrice)
    {
        if (estPrice <= 0) return true;                       // 无法估价 → 放行
        var future = _registry.Resolve(instrumentId);
        if (future == null) return true;
        var cur = _portfolio.GetPosition(instrumentId)?.Quantity ?? 0;
        var next = dir == OrderDirection.Buy ? cur + quantity : cur - quantity;
        var addedLots = Math.Abs(next) - Math.Abs(cur);
        if (addedLots <= 0) return true;                      // 平仓/减仓 → 放行
        var marginRate = future.MarginRate > 0 ? future.MarginRate : 0.08m;
        var margin = estPrice * future.TradingUnit * addedLots * marginRate;
        var fee = future.OpenFee(estPrice, addedLots);
        return _portfolio.Cash >= margin + fee;
    }

    private decimal LastClose(string instrumentId)
    {
        for (int i = _barHistory.Count - 1; i >= 0; i--)
            if (_barHistory[i].InstrumentId == instrumentId)
                return (decimal)_barHistory[i].CloseDouble;
        return 0;
    }

    // ═══ 仓位 ═══
    public override Position? GetPosition(string instrumentId) =>
        _portfolio.GetPosition(instrumentId);

    public override IReadOnlyList<Position> Positions => _portfolio.AllPositions;
    public override decimal Equity => _portfolio.Equity;
    public override decimal AvailableCash => _portfolio.Cash;

    // ═══ 品种 ═══
    public override Future GetFuture(string instrumentId) =>
        _registry.Resolve(instrumentId)!;

    // ═══ 策略信号日志 (Serilog 结构化) ═══
    public override void Log(string message) =>
        _log.LogInformation("[{Strategy}] {Message}", StrategyId, message);

    public override void LogWarning(string message) =>
        _log.LogWarning("[{Strategy}] ⚠ {Message}", StrategyId, message);

    public override void LogError(string message) =>
        _log.LogError("[{Strategy}] ✗ {Message}", StrategyId, message);
}
