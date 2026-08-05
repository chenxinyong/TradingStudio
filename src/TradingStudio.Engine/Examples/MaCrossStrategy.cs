using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// 双均线趋势跟踪 — ATR动态仓位 + 追踪止损。
/// SMA金叉做多/死叉做空，反向穿越或追踪止损出场。
/// 仓位: 风险金额/(ATR止损距离×合约乘数)，受保证金上限25%约束。
///
/// 指标管理: SMA走IndicatorManager（共享/去重），ATR策略内自算。
/// </summary>
public class MaCrossStrategy : IStrategy
{
    [StrategyParameter(Description = "快线周期", DefaultValue = 10, Min = 2, Max = 60, Category = "Entry")]
    public int FastPeriod { get; set; } = 10;

    [StrategyParameter(Description = "慢线周期", DefaultValue = 30, Min = 5, Max = 200, Category = "Entry")]
    public int SlowPeriod { get; set; } = 30;

    [StrategyParameter(Description = "ATR周期", DefaultValue = 20, Min = 10, Max = 40, Category = "Risk")]
    public int AtrPeriod { get; set; } = 20;

    [StrategyParameter(Description = "止损ATR倍数", DefaultValue = 2.0, Min = 1.0, Max = 5.0, Category = "Risk")]
    public double StopAtrMult { get; set; } = 2.0;

    [StrategyParameter(Description = "止盈ATR倍数 (0=关闭止盈, 建议2.5)", DefaultValue = 2.5, Min = 0, Max = 10.0, Category = "Risk")]
    public double TakeProfitAtrMult { get; set; } = 2.5;

    [StrategyParameter(Description = "单笔风险占比", DefaultValue = 0.02, Min = 0.005, Max = 0.05, Category = "Position")]
    public double RiskPerTrade { get; set; } = 0.02;

    [StrategyParameter(Description = "最大保证金占比", DefaultValue = 0.25, Min = 0.10, Max = 0.50, Category = "Position")]
    public double MaxMarginRatio { get; set; } = 0.25;

    [StrategyParameter(Description = "最大持仓手数", DefaultValue = 2, Min = 1, Max = 20, Category = "Position")]
    public int MaxPosition { get; set; } = 2;

    [StrategyParameter(Description = "ADX趋势过滤周期 (0=关闭)", DefaultValue = 14, Min = 0, Max = 30, Category = "Filter")]
    public int AdxPeriod { get; set; } = 14;

    [StrategyParameter(Description = "最低ADX (ADX<此值不交易, 0=关闭)", DefaultValue = 20, Min = 0, Max = 50, Category = "Filter")]
    public int MinAdx { get; set; } = 20;

    [StrategyParameter(Description = "日线趋势过滤 (仅日线MA向上做多)", DefaultValue = false, Category = "Filter")]
    public bool DailyTrendFilter { get; set; } = false;

    [StrategyParameter(Description = "日线趋势MA周期", DefaultValue = 50, Min = 20, Max = 200, Category = "Filter")]
    public int DailyTrendPeriod { get; set; } = 50;

    public string Name => "双均线趋势跟踪(ATR风控)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();

    // 注册的 SMA 指标引用（用于预热期手动 Feed）
    private readonly Dictionary<string, IIndicator> _fastSmas = new();
    private readonly Dictionary<string, IIndicator> _slowSmas = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        foreach (var inst in context.SubscribedInstruments)
        {
            var history = context.GetBarHistory(inst);
            if (history.Count < SlowPeriod + AtrPeriod)
                context.LogWarning($"{inst}: 预热历史不足 ({history.Count}<{SlowPeriod + AtrPeriod})，指标将在回测期内自然就绪后才产生信号");

            // 注册 SMA 指标到 IndicatorManager（多策略共享去重）
            var fastTag = FastPeriod.ToString();
            var slowTag = SlowPeriod.ToString();
            var fastSma = context.RegisterIndicator(inst, new SmaIndicator(FastPeriod), fastTag);
            var slowSma = context.RegisterIndicator(inst, new SmaIndicator(SlowPeriod), slowTag);
            _fastSmas[inst] = fastSma;
            _slowSmas[inst] = slowSma;

            // 预热：手动 Feed 指标 + ATR + ADX + Trend
            var trendPeriod = DailyTrendFilter ? DailyTrendPeriod : 0;
            var s = new InstrumentState(AtrPeriod, AdxPeriod, trendPeriod);
            foreach (var bar in history)
            {
                fastSma.Update(bar);
                slowSma.Update(bar);
                s.UpdateAtr(bar);
                s.UpdateAdx(bar);
                s.UpdateTrendSma(bar);
            }
            _state[inst] = s;

            // 保存预热结束时的 SMA 值，作为实盘首根 Bar 的"前值"
            s.PrevFast = context.GetIndicatorValue(inst, "SMA", fastTag);
            s.PrevSlow = context.GetIndicatorValue(inst, "SMA", slowTag);
            context.Log($"{inst}: Fast={s.PrevFast:F2} Slow={s.PrevSlow:F2} ATR={s.Atr:F2} ({history.Count} bars)");
        }
        _ctx.Log($"初始化: {Name} [{string.Join(", ", context.SubscribedInstruments)}]");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        // 预热期：手动 Feed 指标
        if (_ctx.IsWarmup)
        {
            _fastSmas[bar.InstrumentId].Update(bar);
            _slowSmas[bar.InstrumentId].Update(bar);
            s.UpdateAtr(bar);
            s.UpdateAdx(bar);
            s.UpdateTrendSma(bar);
            s.PrevFast = _ctx.GetIndicatorValue(bar.InstrumentId, "SMA", FastPeriod.ToString());
            s.PrevSlow = _ctx.GetIndicatorValue(bar.InstrumentId, "SMA", SlowPeriod.ToString());
            return;
        }

        // 查询当前 SMA 值（IndicatorManager.Feed 在 OnBar 之前已更新，取到的是含当前 Bar 的最新值）
        var fastTag = FastPeriod.ToString();
        var slowTag = SlowPeriod.ToString();
        var curFast = _ctx.GetIndicatorValue(bar.InstrumentId, "SMA", fastTag);
        var curSlow = _ctx.GetIndicatorValue(bar.InstrumentId, "SMA", slowTag);
        if (double.IsNaN(curFast) || double.IsNaN(curSlow)) return;

        // 上一根 Bar 的 SMA 值（保存在 InstrumentState 中，用于穿越检测）
        var prevFast = s.PrevFast;
        var prevSlow = s.PrevSlow;

        // Feed ATR/ADX/趋势SMA（策略内自算；此前 ADX 与趋势SMA 只在预热期更新，回测期内冻结——已修复）
        s.UpdateAtr(bar);
        s.UpdateAdx(bar);
        s.UpdateTrendSma(bar);

        var pos = _ctx.GetPosition(bar.InstrumentId);
        var hasLong = pos is not null && pos.Quantity > 0;
        var hasShort = pos is not null && pos.Quantity < 0;

        // ── 出场 + 反手 ──
        if (hasLong)
        {
            var exit = false; var reverse = false; string reason = "";
            if (TakeProfitAtrMult > 0 && bar.HighDouble >= s.TakeProfit)
                { exit = true; reverse = false; reason = $"止盈 TP@{s.TakeProfit:F0} (+{bar.CloseDouble-s.EntryPrice:F0}pts, {s.BarsHeld}bars)"; }
            else if (curFast < curSlow && prevFast >= prevSlow)
                { exit = true; reverse = true; reason = "死叉反手"; }
            else if (bar.LowDouble <= s.Trail)
                { exit = true; reverse = curFast < curSlow; reason = $"止损 SL@{s.Trail:F0} (-{s.EntryPrice-bar.LowDouble:F0}pts, {s.BarsHeld}bars)"; }
            else
                { var t = bar.CloseDouble - StopAtrMult * s.Atr; if (t > s.Trail) s.Trail = t; }

            if (exit)
            {
                var exitCode = ExitCode(reason);
                var ticket = _ctx.ClosePosition(bar.InstrumentId, exitCode); _ctx.Log($"多头出场: {bar.InstrumentId} {reason}");
                s.ResetTrade();
                if (reverse)
                {
                    // 延迟反手：平仓成交确认后 OnOrderEvent 再开反向仓
                    // 修复原 ClosePosition→立即 MarketSell 的双倍仓位 Bug
                    s.PendingReverse = new PendingReverseInfo
                    {
                        CloseOrderId = ticket.OrderId,
                        SignalPrice = bar.CloseDouble,
                        Direction = "Short",
                    };
                }
            }
            else s.BarsHeld++;
        }
        else if (hasShort)
        {
            var exit = false; var reverse = false; string reason = "";
            if (TakeProfitAtrMult > 0 && bar.LowDouble <= s.TakeProfit)
                { exit = true; reverse = false; reason = $"止盈 TP@{s.TakeProfit:F0} (+{s.EntryPrice-bar.CloseDouble:F0}pts, {s.BarsHeld}bars)"; }
            else if (curFast > curSlow && prevFast <= prevSlow)
                { exit = true; reverse = true; reason = "金叉反手"; }
            else if (bar.HighDouble >= s.Trail)
                { exit = true; reverse = curFast > curSlow; reason = $"止损 SL@{s.Trail:F0} (-{bar.HighDouble-s.EntryPrice:F0}pts, {s.BarsHeld}bars)"; }
            else
                { var t = bar.CloseDouble + StopAtrMult * s.Atr; if (t < s.Trail) s.Trail = t; }

            if (exit)
            {
                var exitCode = ExitCode(reason);
                var ticket = _ctx.ClosePosition(bar.InstrumentId, exitCode); _ctx.Log($"空头出场: {bar.InstrumentId} {reason}");
                s.ResetTrade();
                if (reverse)
                {
                    // 延迟反手：平仓成交确认后 OnOrderEvent 再开反向仓
                    s.PendingReverse = new PendingReverseInfo
                    {
                        CloseOrderId = ticket.OrderId,
                        SignalPrice = bar.CloseDouble,
                        Direction = "Long",
                    };
                }
            }
            else s.BarsHeld++;
        }

        // ── 入场（仅首次开仓，反手已在出场段处理）──
        if (!hasLong && !hasShort)
        {
            // 1min K线 ATR 极小(~1-2点/8000≈0.00025)，0.003 会过滤全部信号 → 降至 0.0001
            if (s.Atr / bar.CloseDouble < 0.0001) return;

            // ADX 趋势过滤: ADX低于阈值 → 震荡市，不交易
            if (MinAdx > 0 && s.Adx < MinAdx) return;

            // 金叉做多
            if (prevFast <= prevSlow && curFast > curSlow)
            {
                // 已有入场/持仓则跳过
                if (s.Direction != null) return;
                // 日线趋势过滤: 仅日线上升时做多
                if (DailyTrendFilter && s.TrendSma > 0 && bar.CloseDouble < s.TrendSma) return;

                var q = PositionSizer.FromAtrStop(bar.CloseDouble, s.Atr, StopAtrMult,
                    _ctx.GetFuture(bar.InstrumentId), (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
                    RiskPerTrade, (double)MaxMarginRatio, MaxPosition);
                if (q > 0)
                {
                    var filterInfo = MinAdx > 0 ? $" ADX={s.Adx:F0}" : "";
                    _ctx.MarketBuy(bar.InstrumentId, q, $"金叉{filterInfo}");
                    s.Trail = bar.CloseDouble - StopAtrMult * s.Atr;
                    s.TakeProfit = TakeProfitAtrMult > 0 ? bar.CloseDouble + TakeProfitAtrMult * s.Atr : 0;
                    s.EntryPrice = bar.CloseDouble; s.Direction = "Long"; s.BarsHeld = 0;
                    if (TakeProfitAtrMult > 0)
                        _ctx.Log($"多头入场: {bar.InstrumentId} @{bar.CloseDouble:F0} SL={s.Trail:F0} TP={s.TakeProfit:F0} (R={TakeProfitAtrMult/StopAtrMult:F1}:1){filterInfo}");
                }
            }
            // 死叉做空
            else if (prevFast >= prevSlow && curFast < curSlow)
            {
                // 已有入场/持仓则跳过
                if (s.Direction != null) return;
                // 日线趋势过滤: 仅日线下降时做空
                if (DailyTrendFilter && s.TrendSma > 0 && bar.CloseDouble > s.TrendSma) return;

                var q = PositionSizer.FromAtrStop(bar.CloseDouble, s.Atr, StopAtrMult,
                    _ctx.GetFuture(bar.InstrumentId), (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
                    RiskPerTrade, (double)MaxMarginRatio, MaxPosition);
                if (q > 0)
                {
                    var filterInfo = MinAdx > 0 ? $" ADX={s.Adx:F0}" : "";
                    _ctx.MarketSell(bar.InstrumentId, q, $"死叉{filterInfo}");
                    s.Trail = bar.CloseDouble + StopAtrMult * s.Atr;
                    s.TakeProfit = TakeProfitAtrMult > 0 ? bar.CloseDouble - TakeProfitAtrMult * s.Atr : 0;
                    s.EntryPrice = bar.CloseDouble; s.Direction = "Short"; s.BarsHeld = 0;
                    if (TakeProfitAtrMult > 0)
                        _ctx.Log($"空头入场: {bar.InstrumentId} @{bar.CloseDouble:F0} SL={s.Trail:F0} TP={s.TakeProfit:F0} (R={TakeProfitAtrMult/StopAtrMult:F1}:1){filterInfo}");
                }
            }
        }

        // 保存当前 SMA 值作为下一根 Bar 的"前值"（穿越检测用）
        s.PrevFast = curFast;
        s.PrevSlow = curSlow;
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (!_state.TryGetValue(evt.InstrumentId, out var s)) return;

        // 订单被拒：复位入场状态，允许下一根 Bar 重新发单
        if (evt.Type == OrderEventType.Rejected)
        {
            if (s.PendingReverse != null && s.PendingReverse.CloseOrderId == evt.OrderId)
                s.PendingReverse = null;
            else
                s.ResetTrade();
            _ctx.Log($"订单被拒 #{evt.OrderId} {evt.Message} → 状态复位");
            return;
        }

        if (evt.Type != OrderEventType.Filled) return;

        // 平仓确认 → 反向开仓（修复原 ClosePosition→立即 MarketBuy 的双倍仓位 Bug）
        if (s.PendingReverse != null && s.PendingReverse.CloseOrderId != 0
            && evt.OrderId == s.PendingReverse.CloseOrderId)
        {
            var r = s.PendingReverse;
            s.PendingReverse = null; // 清除，防重复触发

            var future = _ctx.GetFuture(evt.InstrumentId);
            var equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : 100_000);
            var q = PositionSizer.FromAtrStop(r.SignalPrice, s.Atr, StopAtrMult, future, equity, RiskPerTrade, (double)MaxMarginRatio, MaxPosition);
            if (q <= 0) return;

            if (r.Direction == "Short")
            {
                _ctx.MarketSell(evt.InstrumentId, q, "反手");
                s.Trail = r.SignalPrice + StopAtrMult * s.Atr;
                s.TakeProfit = TakeProfitAtrMult > 0 ? r.SignalPrice - TakeProfitAtrMult * s.Atr : 0;
            }
            else
            {
                _ctx.MarketBuy(evt.InstrumentId, q, "反手");
                s.Trail = r.SignalPrice - StopAtrMult * s.Atr;
                s.TakeProfit = TakeProfitAtrMult > 0 ? r.SignalPrice + TakeProfitAtrMult * s.Atr : 0;
            }
            s.EntryPrice = r.SignalPrice;
            s.Direction = r.Direction;
            s.BarsHeld = 0;
            _ctx.Log($"反手开仓: {evt.InstrumentId} @{r.SignalPrice:F0} SL={s.Trail:F0} ({r.Direction})");
        }
    }

    public void OnEndOfAlgorithm() { }

    /// <summary>品种状态 — ATR + ADX + 止损/止盈 + 退出追踪</summary>
    private class InstrumentState
    {
        private readonly AtrIndicator _atrIndicator;
        private readonly int _an, _adxn;
        private readonly Queue<double> _trq;
        private double _ts, _prev = double.NaN;
        // ADX
        private readonly List<double> _dmP, _dmM, _trAdx;
        private double _ph = double.NaN, _pl = double.NaN, _pc = double.NaN;
        // Trend SMA (日线代理)
        private readonly Queue<double> _trendPrices;
        private readonly int _trendN;

        public double Atr, Trail;
        public double TakeProfit, EntryPrice;
        public string? Direction;
        public int BarsHeld;
        public double PrevFast = double.NaN, PrevSlow = double.NaN;
        public double Adx;              // 当前ADX值
        public double TrendSma;         // 日线趋势代理SMA
        public PendingReverseInfo? PendingReverse; // 平仓确认后延迟反手

        public InstrumentState(int atrPeriod, int adxPeriod = 0, int trendPeriod = 0)
        {
            _atrIndicator = new AtrIndicator(atrPeriod);
            _an = atrPeriod; _trq = new(atrPeriod + 1);
            _adxn = adxPeriod;
            _dmP = new(adxPeriod); _dmM = new(adxPeriod); _trAdx = new(adxPeriod);
            _trendN = trendPeriod;
            _trendPrices = new(trendPeriod > 0 ? trendPeriod + 1 : 1);
        }

        public void UpdateAtr(Bar bar)
        {
            _atrIndicator.Update(bar);
            if (_atrIndicator.IsReady) Atr = _atrIndicator.CurrentValue;
        }

        public void UpdateAdx(Bar bar)
        {
            if (_adxn <= 0) return;
            if (!double.IsNaN(_ph))
            {
                var up = bar.HighDouble - _ph;
                var down = _pl - bar.LowDouble;
                double plusDM = (up > down && up > 0) ? up : 0;
                double minusDM = (down > up && down > 0) ? down : 0;
                var tr = Math.Max(bar.HighDouble - bar.LowDouble,
                    Math.Max(Math.Abs(bar.HighDouble - _pc), Math.Abs(bar.LowDouble - _pc)));
                _dmP.Add(plusDM); _dmM.Add(minusDM); _trAdx.Add(tr);
                if (_dmP.Count > _adxn) { _dmP.RemoveAt(0); _dmM.RemoveAt(0); _trAdx.RemoveAt(0); }
                if (_dmP.Count >= _adxn && _trAdx.Sum() > 0)
                {
                    var diP = _dmP.Sum() / _trAdx.Sum() * 100;
                    var diM = _dmM.Sum() / _trAdx.Sum() * 100;
                    Adx = Math.Abs(diP - diM) / (diP + diM) * 100;
                }
            }
            _ph = bar.HighDouble; _pl = bar.LowDouble; _pc = bar.CloseDouble;
        }

        public void UpdateTrendSma(Bar bar)
        {
            if (_trendN <= 0) return;
            _trendPrices.Enqueue(bar.CloseDouble);
            if (_trendPrices.Count > _trendN) _trendPrices.Dequeue();
            if (_trendPrices.Count >= _trendN)
                TrendSma = _trendPrices.Average();
        }

        public void ResetTrade()
        {
            Trail = 0; TakeProfit = 0; EntryPrice = 0;
            Direction = null; BarsHeld = 0;
            PendingReverse = null;
        }
    }

    /// <summary>延迟反手：平仓成交确认后，OnOrderEvent 再开反向仓</summary>
    private sealed class PendingReverseInfo
    {
        public long CloseOrderId;
        public double SignalPrice;
        public string Direction = "";  // "Long" or "Short"
    }

    /// <summary>从描述性退出原因提取标准化退出代码: SL/TP/Signal</summary>
    private static string ExitCode(string reason)
    {
        if (reason.Contains("止盈")) return "TP";
        if (reason.Contains("止损")) return "SL";
        if (reason.Contains("死叉") || reason.Contains("金叉")) return "Signal";
        return reason;
    }
}
