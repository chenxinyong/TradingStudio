using System.Threading.Channels;
using TradingStudio.Core.Engine;

namespace TradingStudio.Live;

/// <summary>
/// CTP 交易桥接 — 连接 ExecutionHandler.SendToExchange 与 CTP TraderApi。
/// Phase 3a: 订单发出 + 回报接收。具体 CTP 字段映射待 C++/CLI TraderApi 封装确认后补全。
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

    /// <summary>连接 + 登录交易前置机</summary>
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

        // TODO Phase 3a: CTP 回报 → OrderEvent
        // _trader.OnRtnOrder += ctpOrder => {
        //     var evt = ConvertOrder(ctpOrder);
        //     if (evt != null) _fillWriter.TryWrite(evt);
        // };
        // _trader.OnRtnTrade += ctpTrade => {
        //     var evt = ConvertTrade(ctpTrade);
        //     if (evt != null) _fillWriter.TryWrite(evt);
        // };

        _trader.OnError += (err, _) =>
        {
            System.Diagnostics.Debug.WriteLine($"[CTP] Error: {err.ErrorID} {err.ErrorMsg}");
        };

        _trader.Connect(_opts.TraderFront);
    }

    /// <summary>ExecutionHandler 集成点：市价单发往 CTP</summary>
    public void SendOrder(Order order)
    {
        if (!IsReady || _trader == null) return;

        // TODO Phase 3a: 补全 CTP 下单字段映射
        // var ctpOrder = new CTP.OrderField {
        //     InstrumentID = order.InstrumentId,
        //     Direction = order.Direction == OrderDirection.Buy
        //         ? CTP.DirectionType.Buy : CTP.DirectionType.Sell,
        //     OrderPriceType = order.Type == OrderType.Market
        //         ? CTP.OrderPriceType.AnyPrice : CTP.OrderPriceType.LimitPrice,
        //     LimitPrice = order.LimitPrice ?? 0m,
        //     Volume = order.Quantity,
        //     OrderRef = order.OrderId.ToString(),
        // };
        // _trader.ReqOrderInsert(ctpOrder);
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
