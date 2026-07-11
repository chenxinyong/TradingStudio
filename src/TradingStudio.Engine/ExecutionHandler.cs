using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine;

/// <summary>
/// 订单撮合引擎 — 回测和实盘共用。
/// </summary>
public class ExecutionHandler : IExecutionHandler
{
    private readonly List<Order> _activeOrders = new();
    private readonly List<OrderEvent> _orderHistory = new();
    private readonly RiskController _risk;
    private readonly ILogger _log;
    private readonly Dictionary<string, int> _lastCumulativeVolume = new();
    private readonly Dictionary<string, int> _strategyPriority = new();
    private readonly Dictionary<string, LimitRef> _limitRef = new();
    private readonly Dictionary<string, AtrIndicator> _atr = new(); // 逐合约 ATR，用于滑点按波动缩放
    private readonly object _sync = new();
    private long _nextOrderId = 1;

    /// <summary>逐合约跟踪"上一交易日收盘价"，用作当日涨跌停基准（近似前结算价）。</summary>
    private sealed class LimitRef
    {
        public DateOnly Day;
        public double LastClose;
        public double? PrevDayClose;
    }

    public bool IsLive { get; set; }
    public Action<Order>? SendToExchange { get; set; }

    /// <summary>Bar 模式滑点因子：市价单滑点 = max(1 跳, 因子 × ATR)。默认 0.5；设 0 退回固定 1 跳。</summary>
    public decimal SlippageAtrFactor { get; set; } = 0.5m;

    public System.Threading.Channels.Channel<OrderEvent> FillChannel { get; }
        = System.Threading.Channels.Channel.CreateBounded<OrderEvent>(256);

    public System.Threading.Channels.Channel<OrderEvent> OrderOutbox { get; }
        = System.Threading.Channels.Channel.CreateBounded<OrderEvent>(256);

    public IReadOnlyList<Order> ActiveOrders { get { lock (_sync) return _activeOrders.ToList(); } }
    public IReadOnlyList<Order> GetActiveOrders(string strategyId)
    {
        lock (_sync) return _activeOrders.Where(o => o.StrategyId == strategyId).ToList();
    }
    public IReadOnlyList<OrderEvent> OrderHistory { get { lock (_sync) return _orderHistory.ToList(); } }

    public ExecutionHandler(RiskController risk, ILogger<ExecutionHandler>? logger = null)
    {
        _risk = risk;
        _log = logger ?? NullLogger<ExecutionHandler>.Instance;
    }

    /// <summary>注册策略优先级。引擎在注册策略时调用。Priority 越小越优先。</summary>
    public void SetStrategyPriority(string strategyId, int priority)
    {
        _strategyPriority[strategyId] = priority;
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
            IsCloseOrder = order.IsCloseOrder,
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
                _log.LogWarning("[Order] #{Id} {Dir} {Inst} x{Qty} → REJECTED: {Reason}",
                    id, order.Direction, order.InstrumentId, order.Quantity, riskResult.Reason);
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

        _log.LogInformation("[Order] #{Id} {Dir} {Inst} x{Qty} {Type} [{Strategy}] → Submitted",
            id, order.Direction, order.InstrumentId, order.Quantity, order.Type, strategyId);

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
            _log.LogInformation("[Order] #{Id} {Dir} {Inst} → Cancelled [{Strategy}]",
                orderId, order.Direction, order.InstrumentId, order.StrategyId);
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
            .OrderBy(o => _strategyPriority.GetValueOrDefault(o.StrategyId, int.MaxValue))
            .ThenBy(o => o.OrderId)
            .ToList();
        }

        foreach (var order in pending)
        {
            if (remainingVolume <= 0) break;

            var fill = TryMatchTick(order, tick, future, ref remainingVolume);
            if (fill != null)
            {
                fills.Add(fill);
                if (fill.Type == OrderEventType.Filled)
                    _log.LogDebug("[Fill] #{Id} {Dir} {Inst} x{Qty} @ {Price:F2} [{Strategy}]",
                        fill.OrderId, fill.Direction, fill.InstrumentId, fill.FilledQty,
                        fill.FillPrice, fill.StrategyId);
            }
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

        // 涨跌停锁定：涨停无法买入、跌停无法卖出（CTP 采集时按 UpperLimit/LowerLimitPrice 标记 Flags）
        if (tick.IsUpperLimit && order.Direction == OrderDirection.Buy) return null;
        if (tick.IsLowerLimit && order.Direction == OrderDirection.Sell) return null;

        var fillQty = Math.Min(order.Quantity - order.FilledQuantity, remainingVolume);
        if (fillQty <= 0) return null;

        // TickSize 需缩放到 ×10⁷ 以匹配 TickRecord 价格单位
        var tickSizeScaled = (long)(future.TickSize * TickRecord.PriceScale);

        decimal? fillPrice = order.Type switch
        {
            OrderType.Market when order.Direction == OrderDirection.Buy
                => (decimal)(tick.AskPrice1 + tickSizeScaled) / TickRecord.PriceScale,
            OrderType.Market when order.Direction == OrderDirection.Sell
                => (decimal)(tick.BidPrice1 - tickSizeScaled) / TickRecord.PriceScale,
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

        // 锁内获取快照 + 更新/取得当日涨跌停基准价
        double limitRefPrice, atrForSlippage;
        List<Order> pending;
        lock (_sync)
        {
            limitRefPrice = UpdateAndGetLimitRef(bar);
            // 读取 ATR（基于此前的 Bar，无未来函数）；本 Bar 撮合后再更新
            atrForSlippage = _atr.TryGetValue(bar.InstrumentId, out var a) && a.IsReady
                ? a.CurrentValue : double.NaN;
            pending = _activeOrders
                .Where(o => o.InstrumentId == bar.InstrumentId)
                .OrderBy(o => _strategyPriority.GetValueOrDefault(o.StrategyId, int.MaxValue))
                .ThenBy(o => o.OrderId)
                .ToList();
        }

        foreach (var order in pending)
        {
            var fill = MatchBar(order, bar, future, limitRefPrice, atrForSlippage);
            if (fill != null)
            {
                fills.Add(fill);
                lock (_sync) { _orderHistory.Add(fill); }
                _log.LogDebug("[Fill] #{Id} {Dir} {Inst} x{Qty} @ {Price:F2} [{Strategy}]",
                    fill.OrderId, fill.Direction, fill.InstrumentId, fill.FilledQty,
                    fill.FillPrice, fill.StrategyId);
                if (order.FilledQuantity >= order.Quantity)
                    lock (_sync) { _activeOrders.Remove(order); }
            }
        }

        // 撮合后用本 Bar 更新 ATR（供下一根 Bar 的滑点使用，避免未来函数）
        lock (_sync)
        {
            if (!_atr.TryGetValue(bar.InstrumentId, out var atr))
                _atr[bar.InstrumentId] = atr = new AtrIndicator(14);
            atr.Update(bar);
        }

        return fills;
    }

    /// <summary>
    /// 更新并返回该合约当日涨跌停基准价（= 上一交易日收盘价，近似前结算价）。
    /// 首见该合约或尚无前一交易日数据时返回 0，调用方退回本 Bar 开盘价。
    /// 必须在 _sync 锁内调用。
    /// </summary>
    private double UpdateAndGetLimitRef(Bar bar)
    {
        if (!_limitRef.TryGetValue(bar.InstrumentId, out var r))
        {
            _limitRef[bar.InstrumentId] = new LimitRef { Day = bar.TradingDay, LastClose = bar.CloseDouble };
            return 0; // 首见，无前日基准
        }
        if (bar.TradingDay > r.Day)
        {
            r.PrevDayClose = r.LastClose; // 上一交易日最后收盘 → 当日基准，日内保持不变
            r.Day = bar.TradingDay;
        }
        r.LastClose = bar.CloseDouble;
        return r.PrevDayClose ?? 0;
    }

    /// <summary>单笔订单最大成交量占 Bar 成交量的比例（防止吃光整根 Bar）</summary>
    private const double MaxVolumeParticipation = 0.10;

    /// <summary>Bar 模式市价单滑点：max(1 跳, SlippageAtrFactor × ATR)。ATR 未就绪 → 1 跳。</summary>
    private decimal MarketSlippage(Future future, double atr)
    {
        var tick = future.TickSize > 0 ? future.TickSize : 1m;
        if (SlippageAtrFactor <= 0 || double.IsNaN(atr) || atr <= 0) return tick;
        return Math.Max(tick, SlippageAtrFactor * (decimal)atr);
    }

    /// <summary>用 Bar 撮合一个订单。前进偏差防护：用本 Bar Open 成交市价单。</summary>
    private OrderEvent? MatchBar(Order order, Bar bar, Future future, double limitRefPrice, double atrForSlippage)
    {
        // 实盘模式：市价单已在 CTP 成交，本地不撮合
        if (IsLive && order.Type == OrderType.Market) return null;

        decimal fillPrice;
        var requestedQty = order.Quantity - order.FilledQuantity;
        if (requestedQty <= 0) return null;

        // Volume 约束：单笔订单最多成交 Bar.Volume 的 10%（模拟对手方流动性限制）
        var maxFillByVolume = Math.Max(1, (int)(bar.Volume * MaxVolumeParticipation));
        var fillQty = Math.Min(requestedQty, maxFillByVolume);

        // 涨跌停模拟：涨停无法买入、跌停无法卖出（无对手方）。
        // 基准价优先用"上一交易日收盘价"（近似前结算价）；首日/无历史时退回本 Bar 开盘价。
        var limitPct = (double)(future.PriceLimitPct > 0 ? future.PriceLimitPct : 0.10m);
        var refPrice = limitRefPrice > 0 ? limitRefPrice : bar.OpenDouble;
        var tickSize = (double)(future.TickSize > 0 ? future.TickSize : 1m);
        // 交易所涨跌停价对齐到最小变动价位（四舍五入到 tick），并消除浮点边界误差
        var upperLimit = Math.Round(refPrice * (1 + limitPct) / tickSize) * tickSize;
        var lowerLimit = Math.Round(refPrice * (1 - limitPct) / tickSize) * tickSize;
        var atUpperLimit = bar.HighDouble >= upperLimit;
        var atLowerLimit = bar.LowDouble <= lowerLimit;
        // 涨停买不进 / 跌停卖不出（任何订单类型：市价、限价、止损）
        if (order.Direction == OrderDirection.Buy && atUpperLimit) return null;
        if (order.Direction == OrderDirection.Sell && atLowerLimit) return null;

        switch (order.Type)
        {
            case OrderType.Market:
                // 市价单滑点随波动缩放：max(1 跳, 因子 × ATR)；ATR 未就绪退回固定 1 跳。
                // 买在 Open 上方成交、卖在 Open 下方成交（穿越价差的不利方向）。
                var slip = MarketSlippage(future, atrForSlippage);
                fillPrice = order.Direction == OrderDirection.Buy
                    ? (decimal)bar.OpenDouble + slip   // 市价买: Open + 滑点
                    : (decimal)bar.OpenDouble - slip;  // 市价卖: Open - 滑点
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

