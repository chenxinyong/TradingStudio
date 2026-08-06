using System.Threading.Channels;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using Serilog;

namespace TradingStudio.Live;

/// <summary>
/// CTP 交易桥接 v2 — 使用 FtdcNet.CTP (P/Invoke) 替代 C++/CLI CTPWrapper。
/// </summary>
public class CtpTraderBridge : IDisposable
{
    private readonly CtpTraderOptions _opts;
    private readonly ChannelWriter<OrderEvent> _fillWriter;
    private readonly Serilog.ILogger _log;
    private readonly FutureRegistry? _registry;
    private readonly TradingStudio.Engine.TickSnapshot? _snapshot;
    private readonly object _sync = new();
    private CTP.FtdcTdAdapter? _api;
    private int _requestId;
    private int _reconnectDelay;
    private CancellationTokenSource? _reconnectCts;
    private bool _reconnecting;
    private bool _disposed;

    public bool IsReady { get { lock (_sync) return _isReady; } private set { lock (_sync) _isReady = value; } }
    private bool _isReady;

    /// <summary>CTP 持仓查询回调：每次扫描到一个品种的持仓时触发。</summary>
    public event Action<CtpPositionInfo>? OnPositionReceived;

    /// <summary>
    /// SHFE (上期所) 和 INE (上能所) 不接受泛型 Close ('1'), 必须区分 CloseToday / CloseYesterday。
    /// </summary>
    private static bool RequiresExplicitClose(ExchangeCode ex) =>
        ex is ExchangeCode.SHFE or ExchangeCode.INE;

    public CtpTraderBridge(Channel<OrderEvent> fillChannel, CtpTraderOptions opts,
                              Serilog.ILogger? log = null, FutureRegistry? registry = null,
                              TradingStudio.Engine.TickSnapshot? snapshot = null)
    {
        _fillWriter = fillChannel.Writer;
        _opts = opts;
        _registry = registry;
        _snapshot = snapshot;
        _log = (log ?? Serilog.Log.Logger)?.ForContext<CtpTraderBridge>()
            ?? new Serilog.LoggerConfiguration()
                .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger().ForContext<CtpTraderBridge>();
    }

    public void Connect()
    {
        _reconnectCts = new CancellationTokenSource();
        ConnectInternal();
    }

    private void ConnectInternal()
    {
        try { _api?.Release(); } catch { }
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
                ScheduleReconnect();
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
                    // 提取 CTP 交易日（用于平今/平昨判断）
                    if (e.Param != IntPtr.Zero)
                    {
                        try
                        {
                            var login = CTP.Conv.P2S<CTP.ThostFtdcRspUserLoginField>(e.Param);
                            if (!string.IsNullOrEmpty(login.TradingDay) && login.TradingDay.Length >= 8)
                                _tradingDay = new DateOnly(
                                    int.Parse(login.TradingDay[..4]),
                                    int.Parse(login.TradingDay[4..6]),
                                    int.Parse(login.TradingDay[6..8]));
                        }
                        catch { }
                    }
                    IsReady = true;
                    _reconnectDelay = 0;  // 登录成功，复位退避
                    _reconnectAttempts = 0;  // 复位重连计数
                    _pendingReconnect = false;
                    _log.Information("CTP Trader login OK → ConfirmSettlement");
                    _api.ReqSettlementInfoConfirm(new CTP.ThostFtdcSettlementInfoConfirmField
                    { BrokerID = _opts.BrokerId, InvestorID = _opts.UserId }, ++_requestId);
                    // 登录后查询所有持仓（启动 + 重连均触发）
                    QueryPositions();
                }
                else _log.Error("CTP Trader login failed [{Code}] {Msg}", e.RspInfo.ErrorID, e.RspInfo.ErrorMsg);
            }
            else if (e.EventType == CTP.EnumOnRspType.OnRspQryInvestorPosition && e.Param != IntPtr.Zero)
            {
                try
                {
                    var pf = CTP.Conv.P2S<CTP.ThostFtdcInvestorPositionField>(e.Param);
                    int total = pf.Position + pf.YdPosition;
                    if (total != 0 && !string.IsNullOrEmpty(pf.InstrumentID))
                    {
                        // 均价优先级: PositionCost/Volume → OpenAmount/Volume → OpenCost → SettlementPrice → 0
                        double avgPrice = 0;
                        if (total != 0 && pf.PositionCost > 0)
                            avgPrice = pf.PositionCost / total;       // 持仓成本÷手数
                        else if (total != 0 && pf.OpenAmount > 0)
                            avgPrice = pf.OpenAmount / total;         // 开仓金额÷手数
                        else if (pf.OpenCost > 0)
                            avgPrice = pf.OpenCost;                   // 开仓单价(CTP新版字段)
                        else if (pf.SettlementPrice > 0)
                            avgPrice = pf.SettlementPrice;            // 昨结算价(最后手段)
                        var info = new CtpPositionInfo
                        {
                            InstrumentId = pf.InstrumentID,
                            NetPosition = total,
                            OpenCost = avgPrice,
                            UseMargin = pf.UseMargin,
                        };
                        _log.Information("[CTP-Position] {Inst} Net={Net} AvgPx={Cost:F4} Margin={Margin:F2} (PosCost={PC} OpenAmt={OA} OpenCost={OC})",
                            info.InstrumentId, info.NetPosition, info.OpenCost, info.UseMargin,
                            pf.PositionCost, pf.OpenAmount, pf.OpenCost);
                        OnPositionReceived?.Invoke(info);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "CTP position mapping error");
                }
            }
        };

        // ── OnRtn (Order/Trade) ──
        _api.OnRtnEvent += (_, e) =>
        {
            if (e.EventType == CTP.EnumOnRtnType.OnRtnOrder && e.Param != IntPtr.Zero)
            {
                var ord = CTP.Conv.P2S<CTP.ThostFtdcOrderField>(e.Param);
                _log.Information("[CTP-Trader] OnRtnOrder: {Inst} Status={Status} Ref={Ref} VolTraded={Vol} Msg={Msg}",
                    ord.InstrumentID, ord.OrderStatus, ord.OrderRef, ord.VolumeTraded, ord.StatusMsg);
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

        // 开平标志：上期所/上能所必须区分平今/平昨，不能使用泛型 Close ('1')
        var offsetFlag = ResolveOffsetFlag(order);

        // 上期所/上能所不支持市价单(AnyPrice)，转为限价单
        // 用当前快照价格 ± 3跳点，确保成交
        var orderType = order.Type == OrderType.Market
            ? CTP.EnumOrderPriceTypeType.LimitPrice
            : CTP.EnumOrderPriceTypeType.LimitPrice; // SHFE一律限价
        var futures = _registry?.Resolve(order.InstrumentId);
        bool isShfe = futures != null && RequiresExplicitClose(futures.Exchange);
        var limitPrice = (double)(order.LimitPrice ?? 0m);
        if (order.Type == OrderType.Market || isShfe)
        {
            var snapPrice = _snapshot?.Get(order.InstrumentId)?.LastPrice ?? 0;
            if (snapPrice > 0)
            {
                var tick = futures?.TickSize > 0 ? (double)futures.TickSize : 1;
                limitPrice = order.Direction == OrderDirection.Buy
                    ? snapPrice + tick * 3  // 买单略高于市价
                    : snapPrice - tick * 3; // 卖单略低于市价
            }
        }
        var req = new CTP.ThostFtdcInputOrderField
        {
            BrokerID = _opts.BrokerId, InvestorID = _opts.UserId, UserID = _opts.UserId,
            InstrumentID = order.InstrumentId, OrderRef = order.OrderId.ToString(),
            Direction = order.Direction == OrderDirection.Buy ? CTP.EnumDirectionType.Buy : CTP.EnumDirectionType.Sell,
            CombOffsetFlag_0 = offsetFlag,
            OrderPriceType = orderType,
            LimitPrice = limitPrice, VolumeTotalOriginal = order.Quantity,
            VolumeCondition = CTP.EnumVolumeConditionType.AV, TimeCondition = CTP.EnumTimeConditionType.GFD,
            ContingentCondition = CTP.EnumContingentConditionType.Immediately, ForceCloseReason = CTP.EnumForceCloseReasonType.NotForceClose,
            CombHedgeFlag_0 = CTP.EnumHedgeFlagType.Speculation, IsAutoSuspend = 0, UserForceClose = 0,
        };
        int result = api.ReqOrderInsert(req, ++_requestId);
        _log.Information("CTP InsertOrder: #{Id} {Dir} {Inst} x{Qty} Offset={Offset} (result={Result})",
            order.OrderId, order.Direction, order.InstrumentId, order.Quantity, offsetFlag, result);
    }

    /// <summary>
    /// 根据订单方向和品种所属交易所，选择合适的开平标志。
    /// 上期所/上能所平仓必须明确区分 CloseToday / CloseYesterday，不能使用泛型 Close。
    /// 通过 Order.PositionCreatedDate 与 CTP 交易日比较判断平今/平昨。
    /// </summary>
    private CTP.EnumOffsetFlagType ResolveOffsetFlag(Order order)
    {
        if (!order.IsCloseOrder)
            return CTP.EnumOffsetFlagType.Open;

        if (_registry != null)
        {
            var future = _registry.Resolve(order.InstrumentId);
            if (future != null && RequiresExplicitClose(future.Exchange))
            {
                // 比较建仓日期与CTP交易日：同一天→平今，不同→平昨
                var td = _tradingDay;
                if (td != default && order.PositionCreatedDate != default
                    && order.PositionCreatedDate == td)
                    return CTP.EnumOffsetFlagType.CloseToday;
                return CTP.EnumOffsetFlagType.CloseYesterday;
            }
        }
        return CTP.EnumOffsetFlagType.Close;
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

    /// <summary>查询 CTP 所有持仓（启动登录后 / 重连登录后调用）。</summary>
    private void QueryPositions()
    {
        var api = _api;
        if (api == null) return;

        try
        {
            _log.Information("CTP QueryPositions: requesting all positions...");
            api.ReqQryInvestorPosition(new CTP.ThostFtdcQryInvestorPositionField
            {
                BrokerID = _opts.BrokerId,
                InvestorID = _opts.UserId,
                // InstrumentID 留空 = 查询所有品种
            }, ++_requestId);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "CTP QueryPositions failed");
        }
    }

    private static long ParseOrderRef(string? r)
    {
        if (string.IsNullOrEmpty(r) || r == "0") return -1;
        return long.TryParse(r, out var id) ? id : -1;
    }

    private int _reconnectAttempts;
    private bool _pendingReconnect;
    private DateOnly _tradingDay;  // CTP 交易日，用于区分平今/平昨

    private async void ScheduleReconnect()
    {
        if (_reconnecting)
        {
            _log.Debug("CTP Trader reconnect already in progress, will retry after current attempt");
            _pendingReconnect = true;
            return;
        }
        _reconnecting = true;
        _reconnectAttempts++;

        // Release old API BEFORE creating new one (avoid double-init)
        try { _api?.Release(); } catch { }
        _api = null;

        _reconnectDelay = Math.Min(300, _reconnectDelay == 0 ? 5 : _reconnectDelay * 2);
        _log.Information("CTP Trader reconnecting in {Delay}s (attempt #{Attempt})...", _reconnectDelay, _reconnectAttempts);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_reconnectDelay), _reconnectCts?.Token ?? CancellationToken.None);
            if (!_disposed)
            {
                ConnectInternal();
                // 等待连接结果：最多 30s，超时或 IsReady 后继续
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!_disposed && !IsReady && DateTime.UtcNow < deadline && !_pendingReconnect)
                    await Task.Delay(500, _reconnectCts?.Token ?? CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error(ex, "CTP Trader reconnect error");
        }
        finally
        {
            _reconnecting = false;
            // 仍未就绪或被请求重新连接 → 继续循环
            if (!_disposed && (_pendingReconnect || !IsReady))
            {
                _pendingReconnect = false;
                ScheduleReconnect();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_sync) { if (_disposed) return; _disposed = true; _reconnectCts?.Cancel(); _reconnectCts?.Dispose(); _api?.Release(); _api = null; }
    }
}
