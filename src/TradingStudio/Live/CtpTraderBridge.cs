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

    /// <summary>CTP 资金账户查询回调：登录后查询到账户权益时触发。</summary>
    public event Action<CtpAccountInfo>? OnAccountReceived;

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
                    var parsed = false;
                    if (e.Param != IntPtr.Zero)
                    {
                        try
                        {
                            var login = CTP.Conv.P2S<CTP.ThostFtdcRspUserLoginField>(e.Param);
                            if (!string.IsNullOrEmpty(login.TradingDay) && login.TradingDay.Length >= 8)
                            {
                                _tradingDay = new DateOnly(
                                    int.Parse(login.TradingDay[..4]),
                                    int.Parse(login.TradingDay[4..6]),
                                    int.Parse(login.TradingDay[6..8]));
                                parsed = true;
                            }
                        }
                        catch { }
                    }
                    if (!parsed)
                    {
                        // e.Param 为空时回退到本地日期（夜盘 CTP 交易日 = 下一自然日）
                        _tradingDay = DateOnly.FromDateTime(DateTime.Today);
                        _log.Warning("CTP TradingDay not available from login, using local date: {Day}", _tradingDay);
                    }
                    IsReady = true;
                    _reconnectDelay = 0;  // 登录成功，复位退避
                    _reconnectAttempts = 0;  // 复位重连计数
                    _pendingReconnect = false;
                    _log.Information("CTP Trader login OK → ConfirmSettlement");
                    // CTP 规范：登录后必须先 ReqSettlementInfoConfirm，等 OnRspSettlementInfoConfirm 确认后
                    // 才能发 ReqQry*（查询持仓/账户）。此前在 login 回调里立即查询，账户查询响应常被丢弃，
                    // 导致 Equity 恢复失效（8/18 实测 OnRspQryTradingAccount 从不触发）。
                    var cfmRet = _api.ReqSettlementInfoConfirm(new CTP.ThostFtdcSettlementInfoConfirmField
                    { BrokerID = _opts.BrokerId, InvestorID = _opts.UserId }, ++_requestId);
                    if (cfmRet != 0)
                        _log.Error("CTP ReqSettlementInfoConfirm failed, ret={Ret}", cfmRet);
                }
                else _log.Error("CTP Trader login failed [{Code}] {Msg}", e.RspInfo.ErrorID, e.RspInfo.ErrorMsg);
            }
            else if (e.EventType == CTP.EnumOnRspType.OnRspSettlementInfoConfirm)
            {
                // 确认结算单成功 → 此时才发持仓/账户查询（正确时序，避免查询被 CTP 拒绝）
                if (e.RspInfo == null || e.RspInfo.ErrorID == 0)
                {
                    _log.Information("CTP SettlementInfo confirm OK → QueryPositions");
                    QueryPositions();
                    // 注意：QueryAccount 不在此处发。CTP 一次只能有一个未处理查询，
                    // 需等持仓查询全部返回（OnRspQryInvestorPosition IsLast=true）后串行发出，
                    // 否则 ReqQryTradingAccount 返回 -2（未处理请求过多，8/19 实测）。
                }
                else
                    _log.Error("CTP SettlementInfo confirm failed [{Code}] {Msg}", e.RspInfo.ErrorID, e.RspInfo.ErrorMsg);
            }
            else if (e.EventType == CTP.EnumOnRspType.OnRspQryInvestorPosition)
            {
                // 持仓查询结束（IsLast=true，CTP 对空结果也回一条终止响应）→ 串行发账户查询
                // （CTP 一次只能有一个未处理查询，否则 ReqQryTradingAccount 返回 -2）
                if (e.IsLast)
                {
                    _log.Information("CTP QueryPositions done (IsLast) → QueryAccount");
                    QueryAccount();
                }

                // 终止响应的 Param 可能为空，仅当有数据时才解析
                if (e.Param == IntPtr.Zero)
                    return;

                try
                {
                    var pf = CTP.Conv.P2S<CTP.ThostFtdcInvestorPositionField>(e.Param);
                    // Position 已表示该日期下的净持仓（今仓=今日净持仓, 昨仓=昨日净持仓）。
                    // YdPosition 是昨仓参考值，勿加总否则同批持仓被 double-count。
                    // 正确做法：仅取 Position，LiveComposer 侧按品种累加今仓+昨仓。
                    int datePosition = pf.Position;
                    if (datePosition != 0 && !string.IsNullOrEmpty(pf.InstrumentID))
                    {
                        // 均价优先级: PositionCost/datePosition → OpenAmount/datePosition → OpenCost → SettlementPrice → 0
                        // CTP 的 PositionCost/OpenAmount/OpenCost 对所有交易所都含合约乘数（= 成交价×手数×交易单位），
                        // 需除以 TradingUnit 得到与行情 Bar 同量纲的单位价格。SettlementPrice 已是单位价格，不需除。
                        // （8/18 实测 CZCE v2609 PosCost=45930=4593×2×5：若只对 SHFE/INE 除乘数会得出 22965 的错误均价）
                        double avgPrice = 0;
                        int absPos = Math.Abs(datePosition);
                        double unitDiv = 1;
                        if (_registry != null)
                        {
                            var fut = _registry.Resolve(pf.InstrumentID);
                            if (fut != null)
                                unitDiv = (double)fut.TradingUnit;
                        }
                        if (absPos != 0 && pf.PositionCost > 0)
                            avgPrice = pf.PositionCost / absPos / unitDiv;       // 持仓成本÷手数÷合约乘数
                        else if (absPos != 0 && pf.OpenAmount > 0)
                            avgPrice = pf.OpenAmount / absPos / unitDiv;         // 开仓金额÷手数÷合约乘数
                        else if (pf.OpenCost > 0)
                            avgPrice = pf.OpenCost / unitDiv;                   // 开仓成本÷合约乘数
                        else if (pf.SettlementPrice > 0)
                            avgPrice = pf.SettlementPrice;                      // 昨结算价(已是单位价格)
                        var info = new CtpPositionInfo
                        {
                            InstrumentId = pf.InstrumentID,
                            NetPosition = datePosition,
                            OpenCost = avgPrice,
                            UseMargin = pf.UseMargin,
                            PositionDate = (char)pf.PositionDate,
                        };
                        _log.Information("[CTP-Position] {Inst} Net={Net} AvgPx={Cost:F4} Margin={Margin:F2} Date={PosDate} (PosCost={PC} OpenAmt={OA} OpenCost={OC})",
                            info.InstrumentId, info.NetPosition, info.OpenCost, info.UseMargin, info.PositionDate,
                            pf.PositionCost, pf.OpenAmount, pf.OpenCost);
                        OnPositionReceived?.Invoke(info);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "CTP position mapping error");
                }
            }
            else if (e.EventType == CTP.EnumOnRspType.OnRspQryTradingAccount && e.Param != IntPtr.Zero)
            {
                try
                {
                    var acc = CTP.Conv.P2S<CTP.ThostFtdcTradingAccountField>(e.Param);
                    var info = new CtpAccountInfo
                    {
                        Balance = acc.Balance,
                        PreBalance = acc.PreBalance,
                        PositionProfit = acc.PositionProfit,
                        CloseProfit = acc.CloseProfit,
                        Available = acc.Available,
                        CurrMargin = acc.CurrMargin,
                    };
                    _log.Information("[CTP-Account] Balance={Balance:F2} PreBalance={Pre:F2} PosProfit={Pos:F2} CloseProfit={Close:F2} Available={Avail:F2} Margin={Margin:F2}",
                        info.Balance, info.PreBalance, info.PositionProfit, info.CloseProfit, info.Available, info.CurrMargin);
                    OnAccountReceived?.Invoke(info);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "CTP account mapping error");
                }
            }
        };

        // ── OnRtn (Order/Trade) ──
        _api.OnRtnEvent += (_, e) =>
        {
            if (e.EventType == CTP.EnumOnRtnType.OnRtnOrder && e.Param != IntPtr.Zero)
            {
                var ord = CTP.Conv.P2S<CTP.ThostFtdcOrderField>(e.Param);
                var refId = ParseOrderRef(ord.OrderRef);

                // 去重：CTP 可能对同一状态重复回调 OnRtnOrder。
                // 跳过 (Status, VolTraded) 与上一次完全相同的回调。
                if (_lastOrderRtn.TryGetValue(refId, out var last)
                    && last.Status == ord.OrderStatus
                    && last.VolTraded == ord.VolumeTraded)
                {
                    _log.Debug("[CTP-Trader] OnRtnOrder dup skipped: {Inst} Status={Status} Ref={Ref}",
                        ord.InstrumentID, ord.OrderStatus, ord.OrderRef);
                    return;
                }
                _lastOrderRtn[refId] = (ord.OrderStatus, ord.VolumeTraded);

                // 防重复写入：同一个 OrderRef 只写入第一个 Submitted 事件。
                // CTP 会多次回调 OnRtnOrder（报单已提交→全部成交），全部映射为 Submitted，
                // 若都写入则 DuckDB PK (order_id, type, event_time) 冲突。
                if (!_submittedEmitted.Add(refId))
                {
                    _log.Debug("[CTP-Trader] OnRtnOrder Submitted skipped (already emitted): {Inst} Status={Status} Ref={Ref}",
                        ord.InstrumentID, ord.OrderStatus, ord.OrderRef);
                }
                else
                {
                    _log.Information("[CTP-Trader] OnRtnOrder: {Inst} Status={Status} Ref={Ref} VolTraded={Vol} Msg={Msg}",
                        ord.InstrumentID, ord.OrderStatus, ord.OrderRef, ord.VolumeTraded, ord.StatusMsg);
                    var evt = ConvertOrder(ord); if (evt != null) _fillWriter.TryWrite(evt);
                }
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
            ExchangeID = futures?.Exchange.ToCtp() ?? "",
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
    /// 优先使用 Order.IsCloseToday（从 CTP PositionDate 直接映射，Live 模式权威来源），
    /// 其次回退到 PositionCreatedDate vs _tradingDay 日期比较（回测模式）。
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
                // ① 优先：CTP PositionDate 直接映射（Live 模式，权威来源，无需日期推断）
                if (order.IsCloseToday.HasValue)
                {
                    var flag = order.IsCloseToday.Value
                        ? CTP.EnumOffsetFlagType.CloseToday
                        : CTP.EnumOffsetFlagType.CloseYesterday;
                    _log.Debug("ResolveOffset(PosDate): {Inst} IsCloseToday={IsToday} → {Offset}",
                        order.InstrumentId, order.IsCloseToday.Value, flag);
                    return flag;
                }

                // ② 回退：日期比较（回测模式）
                var td = _tradingDay;
                var created = order.PositionCreatedDate;
                var isToday = td != default && created != default && created == td;
                _log.Debug("ResolveOffset(Date): {Inst} TradingDay={TD} PosCreated={Created} → {Offset}",
                    order.InstrumentId, td, created, isToday ? "CloseToday" : "CloseYesterday");
                return isToday
                    ? CTP.EnumOffsetFlagType.CloseToday
                    : CTP.EnumOffsetFlagType.CloseYesterday;
            }
        }
        return CTP.EnumOffsetFlagType.Close;
    }

    /// <summary>
    /// 从 CTP 日期+时间字段解析为北京时间的 DateTimeOffset。
    /// CTP 的交易日/成交日期就是交易所日历日，不存在夜盘日期偏移问题。
    /// </summary>
    private static DateTimeOffset ParseCtpTime(string dateStr, string timeStr)
    {
        try
        {
            if (dateStr?.Length >= 8 && timeStr?.Length >= 8)
            {
                var y = int.Parse(dateStr[..4]);
                var m = int.Parse(dateStr[4..6]);
                var d = int.Parse(dateStr[6..8]);
                var h = int.Parse(timeStr[..2]);
                var min = int.Parse(timeStr[3..5]);
                var s = int.Parse(timeStr[6..8]);
                return new DateTimeOffset(y, m, d, h, min, s, TimeSpan.FromHours(8));
            }
        }
        catch { }
        return DateTimeOffset.UtcNow;
    }

    private static OrderEvent? ConvertOrder(CTP.ThostFtdcOrderField o)
    {
        // OnRtnOrder 仅输出状态变更通知，成交事件由 OnRtnTrade→ConvertTrade 独立产生。
        // 否则同一笔成交会产生两份 Filled 事件，导致 PortfolioManager 重复处理。
        var t = o.VolumeTraded > 0;
        var type = o.OrderStatus switch
        {
            CTP.EnumOrderStatusType.AllTraded => OrderEventType.Submitted,  // 成交由 OnRtnTrade 处理
            CTP.EnumOrderStatusType.PartTradedQueueing or CTP.EnumOrderStatusType.PartTradedNotQueueing => OrderEventType.Submitted,
            CTP.EnumOrderStatusType.NoTradeQueueing or CTP.EnumOrderStatusType.NoTradeNotQueueing => OrderEventType.Submitted,
            CTP.EnumOrderStatusType.Canceled => OrderEventType.Cancelled,
            _ => OrderEventType.Submitted,
        };
        return new OrderEvent { OrderId = ParseOrderRef(o.OrderRef), InstrumentId = o.InstrumentID ?? "", Direction = o.Direction == CTP.EnumDirectionType.Buy ? OrderDirection.Buy : OrderDirection.Sell, Quantity = t ? o.VolumeTraded : o.VolumeTotalOriginal, OrderQty = o.VolumeTotalOriginal, FilledQty = o.VolumeTraded, FillPrice = (decimal)(o.LimitPrice > 0 ? o.LimitPrice : 0), Type = type, Message = o.StatusMsg, Time = ParseCtpTime(o.InsertDate, o.InsertTime) };
    }

    private static OrderEvent? ConvertTrade(CTP.ThostFtdcTradeField t)
    {
        if (t.Volume <= 0) return null;
        return new OrderEvent { OrderId = ParseOrderRef(t.OrderRef), InstrumentId = t.InstrumentID ?? "", Direction = t.Direction == CTP.EnumDirectionType.Buy ? OrderDirection.Buy : OrderDirection.Sell, Quantity = t.Volume, OrderQty = t.Volume, FilledQty = t.Volume, Type = OrderEventType.Filled, FillPrice = (decimal)t.Price, Time = ParseCtpTime(t.TradeDate, t.TradeTime) };
    }

    /// <summary>查询 CTP 所有持仓（启动登录后 / 重连登录后调用）。</summary>
    private void QueryPositions()
    {
        var api = _api;
        if (api == null) return;

        try
        {
            _log.Information("CTP QueryPositions: requesting all positions...");
            var ret = api.ReqQryInvestorPosition(new CTP.ThostFtdcQryInvestorPositionField
            {
                BrokerID = _opts.BrokerId,
                InvestorID = _opts.UserId,
                // InstrumentID 留空 = 查询所有品种
            }, ++_requestId);
            if (ret != 0)
                _log.Error("CTP ReqQryInvestorPosition failed, ret={Ret}", ret);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "CTP QueryPositions failed");
        }
    }

    /// <summary>查询 CTP 资金账户（启动登录后 / 重连登录后调用），用于恢复真实权益。</summary>
    private void QueryAccount()
    {
        var api = _api;
        if (api == null) return;

        try
        {
            _log.Information("CTP QueryAccount: requesting trading account...");
            var ret = api.ReqQryTradingAccount(new CTP.ThostFtdcQryTradingAccountField
            {
                BrokerID = _opts.BrokerId,
                InvestorID = _opts.UserId,
            }, ++_requestId);
            if (ret != 0)
                _log.Error("CTP ReqQryTradingAccount failed, ret={Ret}", ret);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "CTP QueryAccount failed");
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

    /// <summary>OnRtnOrder 去重：追踪每个 OrderRef 最后一次回调的 (Status, VolTraded)</summary>
    private readonly Dictionary<long, (CTP.EnumOrderStatusType Status, int VolTraded)> _lastOrderRtn = new();

    /// <summary>已发送首个 Submitted 事件的 OrderRef 集合（防同 Ref 多状态重复写入 DuckDB）</summary>
    private readonly HashSet<long> _submittedEmitted = new();

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
