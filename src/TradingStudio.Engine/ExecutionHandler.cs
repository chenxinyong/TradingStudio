using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine;

/// <summary>
/// 订单撮合引擎 — Bar 回放模式：市价/限价/止损单用下一根 Bar 撮合。
/// </summary>
public class ExecutionHandler : IExecutionHandler
{
    private readonly List<Order> _activeOrders = new();
    private readonly List<OrderEvent> _orderHistory = new();
    private readonly RiskController _risk;
    private readonly Dictionary<string, int> _lastCumulativeVolume = new();
    private long _nextOrderId = 1;

    /// <summary>实盘模式：市价单发往 CTP 而非本地撮合</summary>
    public bool IsLive { get; set; }

    /// <summary>实盘模式：引擎调用此委托将订单发往 CTP。CTP 集成点。</summary>
    public Action<Order>? SendToExchange { get; set; }

    /// <summary>CTP 成交回报通道（实盘模式：CTP 回调写入 FillChannel.Writer，引擎后台消费）</summary>
    public System.Threading.Channels.Channel<OrderEvent> FillChannel { get; }
        = System.Threading.Channels.Channel.CreateBounded<OrderEvent>(256);

    public IReadOnlyList<Order> ActiveOrders => _activeOrders;
    public IReadOnlyList<Order> GetActiveOrders(string strategyId) =>
        _activeOrders.Where(o => o.StrategyId == strategyId).ToList();
    public IReadOnlyList<OrderEvent> OrderHistory => _orderHistory;

    public ExecutionHandler(RiskController risk)
    {
        _risk = risk;
    }

    public OrderTicket Submit(Order order, string strategyId, Core.Risk.IPortfolioState? portfolio = null)
    {
        var id = Interlocked.Increment(ref _nextOrderId);
        order = new Order
        {
            OrderId = id,
            InstrumentId = order.InstrumentId,
            Direction = order.Direction,
            Type = order.Type,
            Quantity = order.Quantity,
            LimitPrice = order.LimitPrice,
            StopPrice = order.StopPrice,
            Tag = order.Tag,
            StrategyId = strategyId,
            Status = OrderStatus.Submitted,
            CreatedTime = DateTimeOffset.UtcNow,
        };

        // 风控检查
        if (portfolio != null)
        {
            var riskResult = _risk.CheckPreOrder(order, portfolio);
            if (!riskResult.Passed)
            {
                order.Status = OrderStatus.Rejected;
                _orderHistory.Add(new OrderEvent
                {
                    OrderId = id, InstrumentId = order.InstrumentId,
                    StrategyId = strategyId, Direction = order.Direction,
                    Quantity = order.Quantity, OrderQty = order.Quantity,
                    FilledQty = 0, Type = OrderEventType.Rejected,
                    Message = riskResult.Reason, Time = DateTimeOffset.UtcNow,
                });
                return new OrderTicket { OrderId = id, Status = OrderStatus.Rejected };
            }
        }

        _activeOrders.Add(order);

        // 实盘模式：发往交易所
        if (IsLive && order.Type == OrderType.Market)
            SendToExchange?.Invoke(order);

        var evt = new OrderEvent
        {
            OrderId = id,
            InstrumentId = order.InstrumentId,
            StrategyId = strategyId,
            Direction = order.Direction,
            Quantity = order.Quantity,
            OrderQty = order.Quantity,
            FilledQty = 0,
            Type = OrderEventType.Submitted,
            Time = DateTimeOffset.UtcNow,
        };
        _orderHistory.Add(evt);

        return new OrderTicket { OrderId = id, Status = OrderStatus.Submitted };
    }

    public bool Cancel(long orderId)
    {
        var order = _activeOrders.FirstOrDefault(o => o.OrderId == orderId);
        if (order == null) return false;

        order.Status = OrderStatus.Cancelled;
        _activeOrders.Remove(order);
        _orderHistory.Add(new OrderEvent
        {
            OrderId = orderId, InstrumentId = order.InstrumentId,
            StrategyId = order.StrategyId, Direction = order.Direction,
            Quantity = order.Quantity, OrderQty = order.Quantity,
            FilledQty = order.FilledQuantity,
            Type = OrderEventType.Cancelled, Time = DateTimeOffset.UtcNow,
        });
        return true;
    }

    public IReadOnlyList<OrderEvent> ProcessTick(TickRecord tick, string instrumentId, Future future)
    {
        var fills = new List<OrderEvent>();

        // 增量成交量（流动性约束）
        int incrementalVolume = GetIncrementalVolume(instrumentId, (int)tick.Volume);
        if (incrementalVolume <= 0) return fills;

        int remainingVolume = incrementalVolume;

        // 按策略优先级排序
        var pending = _activeOrders
            .Where(o => o.InstrumentId == instrumentId)
            .OrderBy(o => o.OrderId)
            .ToList();

        foreach (var order in pending)
        {
            if (remainingVolume <= 0) break;

            var fill = TryMatchTick(order, tick, future, ref remainingVolume);
            if (fill != null)
                fills.Add(fill);
        }

        foreach (var f in fills)
            _activeOrders.RemoveAll(o => o.OrderId == f.OrderId);

        return fills;
    }

    /// <summary>用 Tick 撮合一个订单。Bid/Ask 价格 + 流动性约束。</summary>
    private OrderEvent? TryMatchTick(Order order, TickRecord tick, Future future, ref int remainingVolume)
    {
        // 实盘模式：市价单已在 CTP 成交，本地不撮合
        if (IsLive && order.Type == OrderType.Market) return null;

        var fillQty = Math.Min(order.Quantity - order.FilledQuantity, remainingVolume);
        if (fillQty <= 0) return null;

        decimal? fillPrice = order.Type switch
        {
            OrderType.Market when order.Direction == OrderDirection.Buy
                => (decimal)(tick.AskPrice1 + future.TickSize) / TickRecord.PriceScale,
            OrderType.Market when order.Direction == OrderDirection.Sell
                => (decimal)(tick.BidPrice1 - future.TickSize) / TickRecord.PriceScale,
            OrderType.Limit when order.Direction == OrderDirection.Buy
                && tick.AskPrice1 > 0 && tick.AskPrice1 <= order.LimitPrice * TickRecord.PriceScale
                => (decimal)tick.AskPrice1 / TickRecord.PriceScale,
            OrderType.Limit when order.Direction == OrderDirection.Sell
                && tick.BidPrice1 > 0 && tick.BidPrice1 >= order.LimitPrice * TickRecord.PriceScale
                => (decimal)tick.BidPrice1 / TickRecord.PriceScale,
            OrderType.Stop when order.Direction == OrderDirection.Buy
                && tick.AskPrice1 > 0 && tick.AskPrice1 >= order.StopPrice * TickRecord.PriceScale
                => (decimal)tick.AskPrice1 / TickRecord.PriceScale,
            OrderType.Stop when order.Direction == OrderDirection.Sell
                && tick.BidPrice1 > 0 && tick.BidPrice1 <= order.StopPrice * TickRecord.PriceScale
                => (decimal)tick.BidPrice1 / TickRecord.PriceScale,
            _ => null
        };

        if (fillPrice == null) return null;

        remainingVolume -= fillQty;

        var contractValue = fillPrice.Value * future.TradingUnit * fillQty;
        var fee = Math.Max(1m, contractValue * 0.0001m);

        // 滑点：市价单与理论中间价的偏差
        var midPrice = (tick.BidPrice1 + tick.AskPrice1) / 2m / TickRecord.PriceScale;
        var slippage = Math.Abs(fillPrice.Value - midPrice);

        order.FilledQuantity += fillQty;
        order.AvgFillPrice = fillPrice.Value;
        order.Status = order.FilledQuantity >= order.Quantity
            ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

        return new OrderEvent
        {
            OrderId = order.OrderId,
            InstrumentId = order.InstrumentId,
            StrategyId = order.StrategyId,
            Direction = order.Direction,
            Quantity = fillQty,
            OrderQty = order.Quantity,
            FilledQty = order.FilledQuantity,
            Type = order.Status == OrderStatus.Filled
                ? OrderEventType.Filled : OrderEventType.PartiallyFilled,
            FillPrice = fillPrice.Value,
            Fee = fee,
            Slippage = slippage,
            Time = DateTimeOffset.UtcNow,
        };
    }

    public IReadOnlyList<OrderEvent> ProcessBar(Bar bar, Future future)
    {
        var fills = new List<OrderEvent>();
        var pending = _activeOrders
            .Where(o => o.InstrumentId == bar.InstrumentId)
            .OrderBy(o => o.OrderId)
            .ToList();

        foreach (var order in pending)
        {
            var fill = MatchBar(order, bar, future);
            if (fill != null)
            {
                fills.Add(fill);
                _orderHistory.Add(fill);
                _activeOrders.Remove(order);
            }
        }

        return fills;
    }

    /// <summary>用 Bar 撮合一个订单。前进偏差防护：用本 Bar Open 成交市价单。</summary>
    private OrderEvent? MatchBar(Order order, Bar bar, Future future)
    {
        // 实盘模式：市价单已在 CTP 成交，本地不撮合
        if (IsLive && order.Type == OrderType.Market) return null;

        decimal fillPrice;
        var fillQty = order.Quantity - order.FilledQuantity;
        if (fillQty <= 0) return null;

        switch (order.Type)
        {
            case OrderType.Market:
                // 市价单 → 下一根 Bar Open 成交（策略看不到本 Bar）
                fillPrice = (decimal)bar.OpenDouble;
                break;

            case OrderType.Limit:
                // 限价买单：bar.Low <= limitPrice → 成交
                if (order.LimitPrice == null) return null;
                if (order.Direction == OrderDirection.Buy && (decimal)bar.LowDouble <= order.LimitPrice.Value)
                    fillPrice = (decimal)bar.LowDouble;
                else if (order.Direction == OrderDirection.Sell && (decimal)bar.HighDouble >= order.LimitPrice.Value)
                    fillPrice = (decimal)bar.HighDouble;
                else
                    return null; // 限价未触及
                break;

            case OrderType.Stop:
                // 止损买单：bar.High >= stopPrice → 触发
                if (order.StopPrice == null) return null;
                if (order.Direction == OrderDirection.Buy && (decimal)bar.HighDouble >= order.StopPrice.Value)
                    fillPrice = (decimal)bar.HighDouble;
                else if (order.Direction == OrderDirection.Sell && (decimal)bar.LowDouble <= order.StopPrice.Value)
                    fillPrice = (decimal)bar.LowDouble;
                else
                    return null;
                break;

            default:
                return null;
        }

        // 简单手续费：万分之 1
        var contractValue = fillPrice * future.TradingUnit * fillQty;
        var fee = Math.Max(1m, contractValue * 0.0001m);

        // 滑点 = |成交价 - Open|（市价单用 Open 成交，滑点为 0）
        var slippage = order.Type == OrderType.Market
            ? 0m
            : Math.Abs(fillPrice - (decimal)bar.OpenDouble);

        order.FilledQuantity += fillQty;
        order.AvgFillPrice = fillPrice;
        order.Status = order.FilledQuantity >= order.Quantity
            ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

        return new OrderEvent
        {
            OrderId = order.OrderId,
            InstrumentId = order.InstrumentId,
            StrategyId = order.StrategyId,
            Direction = order.Direction,
            Quantity = fillQty,
            OrderQty = order.Quantity,
            FilledQty = order.FilledQuantity,
            Type = order.Status == OrderStatus.Filled
                ? OrderEventType.Filled : OrderEventType.PartiallyFilled,
            FillPrice = fillPrice,
            Fee = fee,
            Slippage = slippage,
            Time = DateTimeOffset.UtcNow,
        };
    }

    private int GetIncrementalVolume(string instrumentId, int currentCumulative)
    {
        if (!_lastCumulativeVolume.TryGetValue(instrumentId, out var last))
            last = 0;
        int delta = currentCumulative - last;
        if (delta < 0) delta = currentCumulative;
        _lastCumulativeVolume[instrumentId] = currentCumulative;
        return delta;
    }
}
