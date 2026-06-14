using System.Threading.Channels;
using TradingStudio.Core.Engine;

namespace TradingStudio.Live;

/// <summary>
/// CTP 交易桥接 — ExecutionHandler.SendToExchange → CTP InsertOrder。
/// CTP 回报 (OnOrder/OnTrade) → OrderEvent → FillChannel。
/// </summary>
public class CtpTraderBridge : IDisposable
{
    private readonly CtpTraderOptions _opts;
    private readonly ChannelWriter<OrderEvent> _fillWriter;
    private CTP.TraderApi? _trader;
    private bool _disposed;

    public bool IsReady { get; private set; }

    public CtpTraderBridge(Channel<OrderEvent> fillChannel, CtpTraderOptions opts)
    {
        _fillWriter = fillChannel.Writer;
        _opts = opts;
    }

    public void Connect()
    {
        _trader = new CTP.TraderApi();

        _trader.OnFrontConnected += () =>
        {
            _trader.Login(_opts.BrokerId, _opts.UserId, _opts.Password);
        };

        _trader.OnLogin += (err, _) =>
        {
            if (err.IsOK())
            {
                IsReady = true;
                _trader.ConfirmSettlement();
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
            System.Diagnostics.Debug.WriteLine($"[CTP] {err.ErrorID}: {err.ErrorMsg}");

        _trader.Connect(_opts.TraderFront);
    }

    /// <summary>ExecutionHandler.SendToExchange → CTP InsertOrder</summary>
    public void SendOrder(Order order)
    {
        if (!IsReady || _trader == null) return;

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
        var isFilled = ctpOrder.OrderStatus == '0';  // THOST_FTDC_OST_AllTraded
        var isCancelled = ctpOrder.OrderStatus == '5'; // THOST_FTDC_OST_Canceled
        var isRejected = ctpOrder.OrderStatus == '4';  // THOST_FTDC_OST_Rejected

        if (!isFilled && !isCancelled && !isRejected) return null;

        var type = isFilled
            ? (ctpOrder.VolumeTraded >= ctpOrder.VolumeTotalOriginal
                ? OrderEventType.Filled : OrderEventType.PartiallyFilled)
            : isCancelled ? OrderEventType.Cancelled : OrderEventType.Rejected;

        return new OrderEvent
        {
            OrderId = long.TryParse(ctpOrder.OrderRef, out var id) ? id : 0,
            InstrumentId = ctpOrder.InstrumentID ?? "",
            Direction = ctpOrder.Direction == '0' ? OrderDirection.Buy : OrderDirection.Sell,
            Quantity = ctpOrder.VolumeTraded,
            OrderQty = ctpOrder.VolumeTotalOriginal,
            FilledQty = ctpOrder.VolumeTraded,
            Type = type,
            FillPrice = (decimal)ctpOrder.LimitPrice,
            Message = ctpOrder.StatusMsg,
            Time = DateTimeOffset.UtcNow,
        };
    }

    private static OrderEvent? ConvertTrade(CTP.Trade ctpTrade)
    {
        if (ctpTrade.Volume <= 0) return null;

        return new OrderEvent
        {
            OrderId = long.TryParse(ctpTrade.OrderRef, out var id) ? id : 0,
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
