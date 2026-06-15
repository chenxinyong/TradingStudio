using System.Threading.Channels;
using TradingStudio.Core.Engine;
using Serilog;

namespace TradingStudio.Live;

/// <summary>
/// CTP 交易桥接 — ExecutionHandler.SendToExchange → CTP InsertOrder。
/// CTP 回报 (OnOrder/OnTrade) → OrderEvent → FillChannel。
/// 内置断线检测：断开时 IsReady=false，后续订单立即拒绝。
/// </summary>
public class CtpTraderBridge : IDisposable
{
    private readonly CtpTraderOptions _opts;
    private readonly ChannelWriter<OrderEvent> _fillWriter;
    private readonly Serilog.ILogger _log;
    private CTP.TraderApi? _trader;
    private bool _disposed;

    public bool IsReady { get; private set; }

    public CtpTraderBridge(Channel<OrderEvent> fillChannel, CtpTraderOptions opts,
                           Serilog.ILogger? log = null)
    {
        _fillWriter = fillChannel.Writer;
        _opts = opts;
        _log = (log ?? Serilog.Log.Logger).ForContext<CtpTraderBridge>();
    }

    public void Connect()
    {
        _trader = new CTP.TraderApi();

        _trader.OnFrontConnected += () =>
        {
            _log.Information("CTP Trader connected: {Front}", _opts.TraderFront);
            _trader.Login(_opts.BrokerId, _opts.UserId, _opts.Password);
        };

        _trader.OnFrontDisconnected += reason =>
        {
            IsReady = false;
            _log.Warning("CTP Trader disconnected — reconnecting in 5s...");

            // 通知引擎：交易已断
            _fillWriter.TryWrite(new OrderEvent
            {
                Type = OrderEventType.Rejected,
                Message = "CTP交易连接断开",
                Time = DateTimeOffset.UtcNow,
            });

            // 自动重连（5s 后，异常不影响回调线程）
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000);
                    if (!_disposed)
                    {
                        _log.Information("CTP Trader reconnecting...");
                        _trader?.Connect(_opts.TraderFront);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "CTP Trader reconnect failed");
                }
            });
        };

        _trader.OnLogin += (err, _) =>
        {
            if (err.IsOK())
            {
                IsReady = true;
                _log.Information("CTP Trader login OK");
                _trader.ConfirmSettlement();
            }
            else
            {
                _log.Error("CTP Trader login failed: {Err}", err.ErrorMsg);
            }
        };

        // CTP 回报 → OrderEvent
        _trader.OnOrder += ctpOrder =>
        {
            var evt = ConvertOrder(ctpOrder);
            if (evt != null) _fillWriter.TryWrite(evt);
        };

        _trader.OnTrade += ctpTrade =>
        {
            var evt = ConvertTrade(ctpTrade);
            if (evt != null) _fillWriter.TryWrite(evt);
        };

        _trader.OnError += (err, _) =>
            _log.Warning("[CTP-Trader] {ErrorID}: {ErrorMsg}", err.ErrorID, err.ErrorMsg);

        _trader.Connect(_opts.TraderFront);
    }

    /// <summary>ExecutionHandler.SendToExchange → CTP InsertOrder</summary>
    public void SendOrder(Order order)
    {
        if (!IsReady || _trader == null)
        {
            _log.Warning("Order rejected — CTP not ready (OrderId={Id}, Inst={Inst})",
                order.OrderId, order.InstrumentId);
            // 立即拒绝——策略知道订单没成交
            _fillWriter.TryWrite(new OrderEvent
            {
                OrderId = order.OrderId,
                InstrumentId = order.InstrumentId,
                Direction = order.Direction,
                Quantity = order.Quantity,
                Type = OrderEventType.Rejected,
                Message = "CTP交易未就绪",
                Time = DateTimeOffset.UtcNow,
            });
            return;
        }

        var req = new CTP.OrderRequest
        {
            InstrumentID = order.InstrumentId,
            Direction = order.Direction == OrderDirection.Buy
                ? CTP.Direction.Buy : CTP.Direction.Sell,
            PriceType = order.Type == OrderType.Market
                ? CTP.OrderPriceType.AnyPrice : CTP.OrderPriceType.LimitPrice,
            Price = (double)(order.LimitPrice ?? 0m),
            Volume = order.Quantity,
            Offset = CTP.OffsetFlag.Open,
            OrderRef = order.OrderId.ToString(),
        };

        _trader.InsertOrder(req);
    }

    // ── 回报转换 ──

    private static OrderEvent? ConvertOrder(CTP.Order ctpOrder)
    {
        var isCancelled = ctpOrder.OrderStatus == '5';
        var isRejected = ctpOrder.OrderStatus == '4';
        if (!isCancelled && !isRejected) return null;

        var type = isCancelled ? OrderEventType.Cancelled : OrderEventType.Rejected;

        return new OrderEvent
        {
            OrderId = ParseOrderRef(ctpOrder.OrderRef),
            InstrumentId = ctpOrder.InstrumentID ?? "",
            Direction = ctpOrder.Direction == '0' ? OrderDirection.Buy : OrderDirection.Sell,
            Quantity = ctpOrder.VolumeTotalOriginal,
            OrderQty = ctpOrder.VolumeTotalOriginal,
            Type = type,
            Message = ctpOrder.StatusMsg,
            Time = DateTimeOffset.UtcNow,
        };
    }

    private static OrderEvent? ConvertTrade(CTP.Trade ctpTrade)
    {
        if (ctpTrade.Volume <= 0) return null;

        return new OrderEvent
        {
            OrderId = ParseOrderRef(ctpTrade.OrderRef),
            InstrumentId = ctpTrade.InstrumentID ?? "",
            Direction = ctpTrade.Direction == '0' ? OrderDirection.Buy : OrderDirection.Sell,
            Quantity = ctpTrade.Volume,
            OrderQty = ctpTrade.Volume,
            FilledQty = ctpTrade.Volume,
            Type = OrderEventType.Filled,
            FillPrice = (decimal)ctpTrade.Price,
            Time = DateTimeOffset.UtcNow,
        };
    }

    private static long ParseOrderRef(string? orderRef)
    {
        if (string.IsNullOrEmpty(orderRef) || orderRef == "0")
            return -1;  // CTP 未分配 OrderRef
        return long.TryParse(orderRef, out var id) ? id : -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trader?.Dispose();
    }
}

public class CtpTraderOptions
{
    public string TraderFront { get; init; } = "";
    public string BrokerId { get; init; } = "9999";
    public string UserId { get; init; } = "";
    public string Password { get; init; } = "";
}
