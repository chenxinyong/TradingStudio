using System.Threading.Channels;
using TradingStudio.Core.Engine;
using Serilog;

namespace TradingStudio.Live;

/// <summary>
/// CTP 交易桥接 v2 — 使用 FtdcNet.CTP (P/Invoke) 替代 C++/CLI CTPWrapper。
/// </summary>
public class CtpTraderBridgeV2 : IDisposable
{
    private readonly CtpTraderOptions _opts;
    private readonly ChannelWriter<OrderEvent> _fillWriter;
    private readonly Serilog.ILogger _log;
    private readonly object _sync = new();
    private CTP.FtdcTdAdapter? _api;
    private int _requestId;
    private bool _disposed;

    public bool IsReady { get { lock (_sync) return _isReady; } private set { lock (_sync) _isReady = value; } }
    private bool _isReady;

    public CtpTraderBridgeV2(Channel<OrderEvent> fillChannel, CtpTraderOptions opts,
                              Serilog.ILogger? log = null)
    {
        _fillWriter = fillChannel.Writer;
        _opts = opts;
        _log = (log ?? Serilog.Log.Logger)?.ForContext<CtpTraderBridgeV2>()
            ?? new Serilog.LoggerConfiguration()
                .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger().ForContext<CtpTraderBridgeV2>();
    }

    public void Connect()
    {
        if (_api != null) return;
        _api = new CTP.FtdcTdAdapter("");

        // ── OnFront ──
        _api.OnFrontEvent += (_, e) =>
        {
            if (e.EventType == CTP.EnumOnFrontType.OnFrontConnected)
            {
                _log.Information("CTP Trader connected: {Front}", _opts.TraderFront);
                if (!string.IsNullOrEmpty(_opts.AuthCode))
                {
                    _log.Information("CTP Trader authenticating...");
                    _api.ReqAuthenticate(new CTP.ThostFtdcReqAuthenticateField
                    {
                        BrokerID = _opts.BrokerId, UserID = _opts.UserId,
                        AuthCode = _opts.AuthCode, AppID = _opts.AppId ?? "simnow_client_test",
                    }, ++_requestId);
                }
                else DoLogin();
            }
            else if (e.EventType == CTP.EnumOnFrontType.OnFrontDisconnected)
            {
                IsReady = false;
                _log.Warning("CTP Trader disconnected (0x{Reason:X})", e.Reason);
                _fillWriter.TryWrite(new OrderEvent { Type = OrderEventType.Rejected, Message = "CTP交易连接断开", Time = DateTimeOffset.UtcNow });
            }
        };

        // ── OnRsp (login/auth/settlement) ──
        _api.OnRspEvent += (_, e) =>
        {
            if (e.RspInfo != null && e.RspInfo.ErrorID != 0)
                _log.Error("CTP Trader error [{Code}] {Msg}", e.RspInfo.ErrorID, e.RspInfo.ErrorMsg);

            if (e.EventType == CTP.EnumOnRspType.OnRspAuthenticate)
            {
                if (e.RspInfo == null || e.RspInfo.ErrorID == 0)
                { _log.Information("CTP Trader auth OK → Login"); DoLogin(); }
                else _log.Error("CTP Trader auth failed [{Code}] {Msg}", e.RspInfo.ErrorID, e.RspInfo.ErrorMsg);
            }
            else if (e.EventType == CTP.EnumOnRspType.OnRspUserLogin)
            {
                if (e.RspInfo == null || e.RspInfo.ErrorID == 0)
                {
                    IsReady = true;
                    _log.Information("CTP Trader login OK → ConfirmSettlement");
                    _api.ReqSettlementInfoConfirm(new CTP.ThostFtdcSettlementInfoConfirmField
                    { BrokerID = _opts.BrokerId, InvestorID = _opts.UserId }, ++_requestId);
                }
                else _log.Error("CTP Trader login failed [{Code}] {Msg}", e.RspInfo.ErrorID, e.RspInfo.ErrorMsg);
            }
        };

        // ── OnRtn (Order/Trade) ──
        _api.OnRtnEvent += (_, e) =>
        {
            if (e.EventType == CTP.EnumOnRtnType.OnRtnOrder && e.Param != IntPtr.Zero)
            {
                var ord = CTP.Conv.P2S<CTP.ThostFtdcOrderField>(e.Param);
                _log.Information("[CTP-Trader] OnRtnOrder: {Inst} Status={Status} Ref={Ref} VolTraded={Vol}",
                    ord.InstrumentID, ord.OrderStatus, ord.OrderRef, ord.VolumeTraded);
                var evt = ConvertOrder(ord); if (evt != null) _fillWriter.TryWrite(evt);
            }
            else if (e.EventType == CTP.EnumOnRtnType.OnRtnTrade && e.Param != IntPtr.Zero)
            {
                var trd = CTP.Conv.P2S<CTP.ThostFtdcTradeField>(e.Param);
                _log.Information("[CTP-Trader] OnRtnTrade: {Inst} Price={Price} Vol={Vol}",
                    trd.InstrumentID, trd.Price, trd.Volume);
                var evt = ConvertTrade(trd); if (evt != null) _fillWriter.TryWrite(evt);
            }
        };

        _api.OnErrRtnEvent += (_, e) =>
            _log.Warning("[CTP-Trader] ErrRtn: [{Code}] {Msg}", e.RspInfo?.ErrorID, e.RspInfo?.ErrorMsg);

        // ── 连接序列 ──
        _api.SubscribePublicTopic(CTP.EnumTeResumeType.THOST_TERT_QUICK);
        _api.SubscribePrivateTopic(CTP.EnumTeResumeType.THOST_TERT_QUICK);
        _api.RegisterFront(_opts.TraderFront);
        _api.Init();
    }

    private void DoLogin()
    {
        _api!.ReqUserLogin(new CTP.ThostFtdcReqUserLoginField
        { BrokerID = _opts.BrokerId, UserID = _opts.UserId, Password = _opts.Password }, ++_requestId);
    }

    public void SendOrder(Order order)
    {
        CTP.FtdcTdAdapter? api; bool ready;
        lock (_sync) { api = _api; ready = _isReady; }

        if (!ready || api == null)
        {
            _log.Warning("Order rejected — CTP not ready");
            _fillWriter.TryWrite(new OrderEvent { OrderId = order.OrderId, InstrumentId = order.InstrumentId, Direction = order.Direction, Quantity = order.Quantity, Type = OrderEventType.Rejected, Message = "CTP交易未就绪", Time = DateTimeOffset.UtcNow });
            return;
        }

        var req = new CTP.ThostFtdcInputOrderField
        {
            BrokerID = _opts.BrokerId, InvestorID = _opts.UserId, UserID = _opts.UserId,
            InstrumentID = order.InstrumentId, OrderRef = order.OrderId.ToString(),
            Direction = order.Direction == OrderDirection.Buy ? CTP.EnumDirectionType.Buy : CTP.EnumDirectionType.Sell,
            CombOffsetFlag_0 = order.IsCloseOrder ? CTP.EnumOffsetFlagType.Close : CTP.EnumOffsetFlagType.Open,
            OrderPriceType = order.Type == OrderType.Market ? CTP.EnumOrderPriceTypeType.AnyPrice : CTP.EnumOrderPriceTypeType.LimitPrice,
            LimitPrice = (double)(order.LimitPrice ?? 0m), VolumeTotalOriginal = order.Quantity,
            VolumeCondition = CTP.EnumVolumeConditionType.AV, TimeCondition = CTP.EnumTimeConditionType.GFD,
            ContingentCondition = CTP.EnumContingentConditionType.Immediately, ForceCloseReason = CTP.EnumForceCloseReasonType.NotForceClose,
            CombHedgeFlag_0 = CTP.EnumHedgeFlagType.Speculation, IsAutoSuspend = 0, UserForceClose = 0,
        };
        int result = api.ReqOrderInsert(req, ++_requestId);
        _log.Information("CTP InsertOrder: #{Id} {Dir} {Inst} x{Qty} (result={Result})", order.OrderId, order.Direction, order.InstrumentId, order.Quantity, result);
    }

    private static OrderEvent? ConvertOrder(CTP.ThostFtdcOrderField o)
    {
        var t = o.VolumeTraded > 0;
        var type = o.OrderStatus switch
        {
            CTP.EnumOrderStatusType.AllTraded => t ? OrderEventType.Filled : OrderEventType.Submitted,
            CTP.EnumOrderStatusType.PartTradedQueueing or CTP.EnumOrderStatusType.PartTradedNotQueueing => t ? OrderEventType.PartiallyFilled : OrderEventType.Submitted,
            CTP.EnumOrderStatusType.NoTradeQueueing or CTP.EnumOrderStatusType.NoTradeNotQueueing => OrderEventType.Submitted,
            CTP.EnumOrderStatusType.Canceled => OrderEventType.Cancelled,
            _ => OrderEventType.Submitted,
        };
        return new OrderEvent { OrderId = ParseOrderRef(o.OrderRef), InstrumentId = o.InstrumentID ?? "", Direction = o.Direction == CTP.EnumDirectionType.Buy ? OrderDirection.Buy : OrderDirection.Sell, Quantity = t ? o.VolumeTraded : o.VolumeTotalOriginal, OrderQty = o.VolumeTotalOriginal, FilledQty = o.VolumeTraded, FillPrice = (decimal)(o.LimitPrice > 0 ? o.LimitPrice : 0), Type = type, Message = o.StatusMsg, Time = DateTimeOffset.UtcNow };
    }

    private static OrderEvent? ConvertTrade(CTP.ThostFtdcTradeField t)
    {
        if (t.Volume <= 0) return null;
        return new OrderEvent { OrderId = ParseOrderRef(t.OrderRef), InstrumentId = t.InstrumentID ?? "", Direction = t.Direction == CTP.EnumDirectionType.Buy ? OrderDirection.Buy : OrderDirection.Sell, Quantity = t.Volume, OrderQty = t.Volume, FilledQty = t.Volume, Type = OrderEventType.Filled, FillPrice = (decimal)t.Price, Time = DateTimeOffset.UtcNow };
    }

    private static long ParseOrderRef(string? r)
    {
        if (string.IsNullOrEmpty(r) || r == "0") return -1;
        return long.TryParse(r, out var id) ? id : -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_sync) { if (_disposed) return; _disposed = true; _api?.Release(); _api = null; }
    }
}
