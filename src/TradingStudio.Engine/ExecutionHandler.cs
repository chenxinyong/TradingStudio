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
    private readonly object _sync = new();  // 保护 _activeOrders / _orderHistory 并发访问（Submit 和 REST API 可能并发）
    private long _nextOrderId = 1;

    /// <summary>实盘模式：市价单发往 CTP 而非本地撮合</summary>
    public bool IsLive { get; set; }

    /// <summary>实盘模式：引擎调用此委托将订单发往 CTP。CTP 集成点。</summary>
    public Action<Order>? SendToExchange { get; set; }

    /// <summary>CTP 成交回报通道（实盘模式：CTP 回调写入 FillChannel.Writer，引擎独占消费）</summary>
    public System.Threading.Channels.Channel<OrderEvent> FillChannel { get; }
        = System.Threading.Channels.Channel.CreateBounded<OrderEvent>(256);

    /// <summary>已处理的成交事件输出通道（引擎处理完 FillChannel 后写入，SignalR 推送端消费）</summary>
    public System.Threading.Channels.Channel<OrderEvent> OrderOutbox { get; }
        = System.Threading.Channels.Channel.CreateBounded<OrderEvent>(256);

    public IReadOnlyList<Order> ActiveOrders { get { lock (_sync) return _activeOrders.ToList(); } }
    public IReadOnlyList<Order> GetActiveOrders(string strategyId)
    {
        lock (_sync) return _activeOrders.Where(o => o.StrategyId == strategyId).ToList();
    }
    public IReadOnlyList<OrderEvent> OrderHistory { get { lock (_sync) return _orderHistory.ToList(); } }

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
                lock (_sync) { _orderHistory.Add(new OrderEvent
                {
                    OrderId = id, InstrumentId = order.InstrumentId,
                    StrategyId = strategyId, Direction = order.Direction,
                    Quantity = order.Quantity, OrderQty = order.Quantity,
                    FilledQty = 0, Type = OrderEventType.Rejected,
                    Message = riskResult.Reason, Time = DateTimeOffset.UtcNow,
                }); }
                return new OrderTicket { OrderId = id, Status = OrderStatus.Rejected };
            }
        }

        lock (_sync) { _activeOrders.Add(order); }

        // 实盘模式：发往交易所（锁外调用，避免 CTP 回调导致死锁）
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
        lock (_sync) { _orderHistory.Add(evt); }

        return new OrderTicket { OrderId = id, Status = OrderStatus.Submitted };
    }

    public bool Cancel(long orderId)
    {
        lock (_sync)
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
    }

    public IReadOnlyList<OrderEvent> ProcessTick(TickRecord tick, string instrumentId, Future future)
    {
        var fills = new List<OrderEvent>();

        // 增量成交量（流动性约束）
        int incrementalVolume = GetIncrementalVolume(instrumentId, (int)tick.Volume);
        if (incrementalVolume <= 0) return fills;

        int remainingVolume = incrementalVolume;

        // 在锁内获取快照，锁外撮合（避免锁持有时间过长）
        List<Order> pending;
        lock (_sync) { pending = _activeOrders
            .Where(o => o.InstrumentId == instrumentId)
            .OrderBy(o => o.OrderId)
            .ToList();
        }

        foreach (var order in pending)
        {
            if (remainingVolume <= 0) break;

            var fill = TryMatchTick(order, tick, future, ref remainingVolume);
            if (fill != null)
                fills.Add(fill);
        }

        // 移除完全成交的订单
        lock (_sync)
        {
            foreach (var f in fills)
            {
                var order = _activeOrders.FirstOrDefault(o => o.OrderId == f.OrderId);
                if (order != null && order.FilledQuantity >= order.Quantity)
                    _activeOrders.Remove(order);
            }
        }

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
        var rate = (decimal)(future.FeeRate > 0 ? future.FeeRate : 0.0001);
        var fee = Math.Max(1m, contractValue * rate);

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
            Time = DateTimeOffset.FromUnixTimeMilliseconds(tick.ExchangeTimestamp),
        };
    }

    public IReadOnlyList<OrderEvent> ProcessBar(Bar bar, Future future)
    {
        var fills = new List<OrderEvent>();

        // 锁内获取快照
        List<Order> pending;
        lock (_sync) { pending = _activeOrders
            .Where(o => o.InstrumentId == bar.InstrumentId)
            .OrderBy(o => o.OrderId)
            .ToList();
        }

        foreach (var order in pending)
        {
            var fill = MatchBar(order, bar, future);
            if (fill != null)
            {
                fills.Add(fill);
                lock (_sync) { _orderHistory.Add(fill); }
                // MatchBar 已设置 order.FilledQuantity/Status — 仅移除完全成交的
                if (order.FilledQuantity >= order.Quantity)
                    lock (_sync) { _activeOrders.Remove(order); }
            }
        }

        return fills;
    }

    /// <summary>单笔订单最大成交量占 Bar 成交量的比例（防止吃光整根 Bar）</summary>
    private const double MaxVolumeParticipation = 0.10;

    /// <summary>用 Bar 撮合一个订单。前进偏差防护：用本 Bar Open 成交市价单。</summary>
    private OrderEvent? MatchBar(Order order, Bar bar, Future future)
    {
        // 实盘模式：市价单已在 CTP 成交，本地不撮合
        if (IsLive && order.Type == OrderType.Market) return null;

        decimal fillPrice;
        var requestedQty = order.Quantity - order.FilledQuantity;
        if (requestedQty <= 0) return null;

        // Volume 约束：单笔订单最多成交 Bar.Volume 的 10%（模拟对手方流动性限制）
        var maxFillByVolume = Math.Max(1, (int)(bar.Volume * MaxVolumeParticipation));
        var fillQty = Math.Min(requestedQty, maxFillByVolume);

        // 涨跌停模拟：涨停买入/跌停卖出无法成交（无对手方）
        var limitPct = (double)(future.PriceLimitPct > 0 ? future.PriceLimitPct : 0.10m);
        var prevClose = bar.OpenDouble; // 近似：用开盘价替代前结算价
        var upperLimit = prevClose * (1 + limitPct);
        var lowerLimit = prevClose * (1 - limitPct);
        var atUpperLimit = bar.HighDouble >= upperLimit;
        var atLowerLimit = bar.LowDouble <= lowerLimit;

        switch (order.Type)
        {
            case OrderType.Market:
                // 涨停买不进 / 跌停卖不出
                if (order.Direction == OrderDirection.Buy && atUpperLimit) return null;
                if (order.Direction == OrderDirection.Sell && atLowerLimit) return null;
                // 市价单滑点: 买吃Ask(+1跳), 卖砸Bid(-1跳), 最小成本穿越价差
                var tick = future.TickSize > 0 ? future.TickSize : 1m;
                fillPrice = order.Direction == OrderDirection.Buy
                    ? (decimal)bar.OpenDouble + tick   // 市价买: Open + 1跳
                    : (decimal)bar.OpenDouble - tick;  // 市价卖: Open - 1跳
                break;

            case OrderType.Limit:
                // 限价单：以 limitPrice 成交，不超过 Open（前向偏差防护）
                // 限价买单触发条件：bar.Low <= limitPrice；成交价 = min(limitPrice, Open)
                // 限价卖单触发条件：bar.High >= limitPrice；成交价 = max(limitPrice, Open)
                // 修正前用 bar.Low/bar.High 极端价成交 → 回测结果虚高
                if (order.LimitPrice == null) return null;
                var barOpen = (decimal)bar.OpenDouble;
                if (order.Direction == OrderDirection.Buy && (decimal)bar.LowDouble <= order.LimitPrice.Value)
                    fillPrice = Math.Min(order.LimitPrice.Value, barOpen);
                else if (order.Direction == OrderDirection.Sell && (decimal)bar.HighDouble >= order.LimitPrice.Value)
                    fillPrice = Math.Max(order.LimitPrice.Value, barOpen);
                else
                    return null; // 限价未触及
                break;

            case OrderType.Stop:
                // 止损单：触发后以 stopPrice 或 Open 成交（哪个更不利用哪个）
                // 止损买单触发条件：bar.High >= stopPrice；成交价 = max(stopPrice, Open)
                // 止损卖单触发条件：bar.Low <= stopPrice；成交价 = min(stopPrice, Open)
                // 修正前用 bar.High/bar.Low 极端价成交 → 回测中穿越止损价后反弹也不亏
                if (order.StopPrice == null) return null;
                var stopOpen = (decimal)bar.OpenDouble;
                if (order.Direction == OrderDirection.Buy && (decimal)bar.HighDouble >= order.StopPrice.Value)
                    fillPrice = Math.Max(order.StopPrice.Value, stopOpen);
                else if (order.Direction == OrderDirection.Sell && (decimal)bar.LowDouble <= order.StopPrice.Value)
                    fillPrice = Math.Min(order.StopPrice.Value, stopOpen);
                else
                    return null;
                break;

            default:
                return null;
        }

        // 手续费：从品种配置读取费率（默认万1）
        var contractValue = fillPrice * future.TradingUnit * fillQty;
        var rate = (decimal)(future.FeeRate > 0 ? future.FeeRate : 0.0001);
        var fee = Math.Max(1m, contractValue * rate);

        // 滑点 = |成交价 - Open| × 手数（市价单跨价差，限价/止损单执行偏差）
        var slipPerLot = Math.Abs(fillPrice - (decimal)bar.OpenDouble);
        var slippage = slipPerLot * fillQty * future.TradingUnit;

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
            Time = new DateTimeOffset(bar.BarTime, TimeSpan.FromHours(8)),
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

