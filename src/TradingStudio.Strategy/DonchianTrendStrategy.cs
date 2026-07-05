using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Strategy;

/// <summary>
/// ATR通道突破趋势跟踪策略 — 多品种日内中频。
///
/// 核心逻辑:
///   多头: Close突破N根Bar最高价 + MA(50)趋势向上 → 入场
///   空头: Close跌破N根Bar最低价 + MA(50)趋势向下 → 入场
///   止损: 2×ATR 跟踪止损
///   出场: 反向突破M根Bar边界 或 跟踪止损触发
///
/// 参考: Richard Donchian通道 + Ed Seykota趋势跟踪 + ATR动态仓位
/// </summary>
public class DonchianTrendStrategy : IStrategy
{
    // ═══ 策略参数 ═══

    [StrategyParameter(Description = "通道周期(K线数)", DefaultValue = 20, Min = 10, Max = 60, Category = "Entry")]
    public int ChannelPeriod { get; set; } = 20;

    [StrategyParameter(Description = "出场通道周期(K线数)", DefaultValue = 10, Min = 5, Max = 30, Category = "Exit")]
    public int ExitPeriod { get; set; } = 10;

    [StrategyParameter(Description = "趋势MA周期", DefaultValue = 50, Min = 20, Max = 200, Category = "Filter")]
    public int TrendMAPeriod { get; set; } = 50;

    [StrategyParameter(Description = "ATR周期", DefaultValue = 20, Min = 10, Max = 40, Category = "Risk")]
    public int AtrPeriod { get; set; } = 20;

    [StrategyParameter(Description = "止损ATR倍数", DefaultValue = 2.0, Min = 1.0, Max = 4.0, Category = "Risk")]
    public double StopAtrMult { get; set; } = 2.0;

    [StrategyParameter(Description = "止盈ATR倍数 (0=关闭止盈, 建议3.0)", DefaultValue = 3.0, Min = 0, Max = 10.0, Category = "Risk")]
    public double TakeProfitAtrMult { get; set; } = 3.0;

    [StrategyParameter(Description = "最低波动率(ATR/Close) — 15min建议0.001-0.005", DefaultValue = 0.003, Min = 0.001, Max = 0.03, Category = "Filter")]
    public double MinVolatility { get; set; } = 0.003;

    [StrategyParameter(Description = "单笔风险占比", DefaultValue = 0.02, Min = 0.005, Max = 0.05, Category = "Position")]
    public double RiskPerTrade { get; set; } = 0.02;

    [StrategyParameter(Description = "最大持仓K线数(0=不限)", DefaultValue = 0, Min = 0, Max = 500, Category = "Exit")]
    public int MaxBarsInTrade { get; set; } = 0;

    public string Name => "Donchian通道趋势跟踪(ATR动态仓位)";

    // ═══ 内部状态 ═══

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;

        foreach (var inst in context.SubscribedInstruments)
        {
            var history = context.GetBarHistory(inst);
            if (history.Count < TrendMAPeriod + ChannelPeriod)
            {
                context.LogWarning($"{inst}: 历史数据不足 (需要{TrendMAPeriod + ChannelPeriod}, 实际{history.Count})");
                continue;
            }

            var state = new InstrumentState(inst, ChannelPeriod, ExitPeriod, TrendMAPeriod, AtrPeriod);
            _state[inst] = state;

            // 预热：一次性计算 MA + ATR + 通道
            foreach (var bar in history)
                state.Warmup(bar);

            context.Log($"{inst}: MA={state.CurrentMA:F2} ATR={state.CurrentAtr:F4} " +
                       $"ChHi={state.ChannelHigh:F2} ChLo={state.ChannelLow:F2} ({history.Count} bars)");
        }

        _ctx.Log($"初始化: {Name} on [{string.Join(", ", context.SubscribedInstruments)}]");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        // 预热：历史和实时 feed 共享 Feed 方法
        if (_ctx.IsWarmup) { s.Feed(bar); return; }

        s.Feed(bar);

        // 波动率过滤 — ATR 未初始化时跳过
        if (s.CurrentAtr <= 0) return;
        var volatility = s.CurrentAtr / bar.CloseDouble;
        if (volatility < MinVolatility)
        {
            // 仅在前 10 次被过滤时记录诊断日志
            if (s._diagBlocked++ < 10)
                _ctx.Log($"VolFilter: {s.InstrumentId} ATR={s.CurrentAtr:F2} Close={bar.CloseDouble:F0} " +
                         $"Vol={volatility*100:F2}% < Min={MinVolatility*100:F1}%");
            return;
        }

        var pos = _ctx.GetPosition(s.InstrumentId);
        var hasPosition = pos is not null && pos.Quantity != 0;

        // ── 出场逻辑 ──
        if (hasPosition)
        {
            var shouldExit = false;
            var exitReason = "";

            // 止盈 (优先检查)
            if (TakeProfitAtrMult > 0 && s.TakeProfit > 0)
            {
                if (pos!.Quantity > 0 && bar.HighDouble >= s.TakeProfit)
                {
                    shouldExit = true;
                    var profit = bar.CloseDouble - s.EntryPrice;
                    exitReason = $"止盈 TP@{s.TakeProfit:F0} (+{profit:F0}pts, {s.BarsInTrade}bars)";
                }
                else if (pos.Quantity < 0 && bar.LowDouble <= s.TakeProfit)
                {
                    shouldExit = true;
                    var profit = s.EntryPrice - bar.CloseDouble;
                    exitReason = $"止盈 TP@{s.TakeProfit:F0} (+{profit:F0}pts, {s.BarsInTrade}bars)";
                }
            }

            // 反向突破出场
            if (!shouldExit && pos!.Quantity > 0 && bar.CloseDouble < s.ExitLow)
            {
                shouldExit = true;
                exitReason = "反向突破出场(多头)";
            }
            else if (!shouldExit && pos.Quantity < 0 && bar.CloseDouble > s.ExitHigh)
            {
                shouldExit = true;
                exitReason = "反向突破出场(空头)";
            }
            // 跟踪止损
            else if (!shouldExit && pos.Quantity > 0 && bar.LowDouble <= s.TrailingStop)
            {
                shouldExit = true;
                var loss = s.EntryPrice - s.TrailingStop;
                exitReason = $"止损 SL@{s.TrailingStop:F0} (-{loss:F0}pts, {s.BarsInTrade}bars)";
            }
            else if (!shouldExit && pos.Quantity < 0 && bar.HighDouble >= s.TrailingStop)
            {
                shouldExit = true;
                var loss = s.TrailingStop - s.EntryPrice;
                exitReason = $"止损 SL@{s.TrailingStop:F0} (-{loss:F0}pts, {s.BarsInTrade}bars)";
            }
            // 时间出场
            else if (!shouldExit && MaxBarsInTrade > 0 && s.BarsInTrade >= MaxBarsInTrade)
            {
                shouldExit = true;
                exitReason = $"持仓超时 ({s.BarsInTrade}bars)";
            }

            if (shouldExit)
            {
                _ctx.ClosePosition(s.InstrumentId);
                _ctx.Log($"出场: {s.InstrumentId} {exitReason} @ {bar.CloseDouble:F2}");
                s.ResetTrade();
                return;
            }

            // 更新跟踪止损
            if (pos.Quantity > 0)
            {
                var newStop = bar.CloseDouble - StopAtrMult * s.CurrentAtr;
                if (newStop > s.TrailingStop) s.TrailingStop = newStop;
            }
            else
            {
                var newStop = bar.CloseDouble + StopAtrMult * s.CurrentAtr;
                if (newStop < s.TrailingStop) s.TrailingStop = newStop;
            }

            s.BarsInTrade++;
        }

        // ── 入场逻辑 ──
        if (!hasPosition)
        {
            var trendUp = s.CurrentMA > s.PrevMA;
            var trendDown = s.CurrentMA < s.PrevMA;

            // 诊断（前5根 Bar）
            if (s._diag++ < 5)
                _ctx.Log($"Diag: Close={bar.CloseDouble:F0} ChHi={s.ChannelHigh:F0} ChLo={s.ChannelLow:F0} " +
                    $"MA={s.CurrentMA:F0} Trend={(trendUp?"U":trendDown?"D":"-")} " +
                    $"Vol={s.CurrentAtr/bar.CloseDouble*100:F1}%");

            // 多头: 价格突破通道高点 + MA趋势向上
            if (bar.CloseDouble > s.ChannelHigh && trendUp)
            {
                var qty = CalculateLots(bar.CloseDouble, s);
                if (qty > 0)
                {
                    _ctx.MarketBuy(s.InstrumentId, qty, $"突破入场: {bar.CloseDouble:F0}>{s.ChannelHigh:F0}");
                    s.TrailingStop = bar.CloseDouble - StopAtrMult * s.CurrentAtr;
                    s.TakeProfit = TakeProfitAtrMult > 0 ? bar.CloseDouble + TakeProfitAtrMult * s.CurrentAtr : 0;
                    s.EntryPrice = bar.CloseDouble; s.Direction = "Long"; s.BarsInTrade = 0;
                    if (TakeProfitAtrMult > 0)
                        _ctx.Log($"多头入场: {s.InstrumentId} @{bar.CloseDouble:F0} SL={s.TrailingStop:F0} TP={s.TakeProfit:F0} (R={TakeProfitAtrMult/StopAtrMult:F1}:1)");
                }
            }
            // 空头: 价格跌破通道低点 + MA趋势向下
            else if (bar.CloseDouble < s.ChannelLow && trendDown)
            {
                var qty = CalculateLots(bar.CloseDouble, s);
                if (qty > 0)
                {
                    _ctx.MarketSell(s.InstrumentId, qty, $"突破入场: <{s.ChannelLow:F0}");
                    s.TrailingStop = bar.CloseDouble + StopAtrMult * s.CurrentAtr;
                    s.TakeProfit = TakeProfitAtrMult > 0 ? bar.CloseDouble - TakeProfitAtrMult * s.CurrentAtr : 0;
                    s.EntryPrice = bar.CloseDouble; s.Direction = "Short"; s.BarsInTrade = 0;
                    if (TakeProfitAtrMult > 0)
                        _ctx.Log($"空头入场: {s.InstrumentId} @{bar.CloseDouble:F0} SL={s.TrailingStop:F0} TP={s.TakeProfit:F0} (R={TakeProfitAtrMult/StopAtrMult:F1}:1)");
                }
            }
        }
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Filled)
            _ctx.Log($"成交: {evt.InstrumentId} {evt.Direction} {evt.Quantity}手 @ {evt.FillPrice:F2}");
    }

    public void OnEndOfAlgorithm()
    {
        foreach (var (inst, _) in _state)
        {
            var pos = _ctx.GetPosition(inst);
            _ctx.Log($"{inst}: Equity={_ctx.Equity:C} Position={pos?.Quantity ?? 0}");
        }
    }

    // ═══ 仓位计算 ═══

    private int CalculateLots(double price, InstrumentState s)
    {
        if (s.CurrentAtr <= 0) return 0;

        var future = _ctx.GetFuture(s.InstrumentId);
        var mult = (double)(future?.TradingUnit ?? 1m);
        var totalEquity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        var riskAmount = totalEquity * RiskPerTrade;
        var stopDist = StopAtrMult * s.CurrentAtr;

        if (stopDist < price * 0.005) return 0;

        int lots = (int)(riskAmount / (stopDist * mult));
        if (lots < 1) lots = 1;

        // 保证金限制
        var marginRate = (double)(future?.MarginRate ?? 0.08m);
        var marginPerLot = price * mult * marginRate;
        var maxMargin = totalEquity * 0.5;
        while (lots > 1 && marginPerLot * lots > maxMargin)
            lots--;

        return lots;
    }

    // ═══ 品种状态 ═══

    private class InstrumentState
    {
        public string InstrumentId;
        private readonly int _channelN, _exitN, _maN, _atrN;

        // 滑动窗口
        private readonly Queue<double> _highWindow, _lowWindow, _closeWindow;
        private readonly Queue<double> _atrTrueRanges;
        private double _atrSum, _maSum;

        // 当前值
        public double CurrentMA { get; private set; }
        public double PrevMA { get; private set; }
        public double CurrentAtr { get; private set; }
        public double ChannelHigh { get; private set; }
        public double ChannelLow { get; private set; }
        public double ExitHigh { get; private set; }
        public double ExitLow { get; private set; }

        // 跟踪止损 + 止盈 + 诊断
        public double TrailingStop;
        public double TakeProfit;      // 止盈目标价
        public double EntryPrice;      // 入场价
        public string? Direction;      // "Long" / "Short"
        public int BarsInTrade;
        public int _diag;
        public int _diagBlocked;

        private double _prevClose = double.NaN;

        public InstrumentState(string inst, int channelN, int exitN, int maN, int atrN)
        {
            InstrumentId = inst;
            _channelN = channelN; _exitN = exitN; _maN = maN; _atrN = atrN;
            _highWindow = new Queue<double>(channelN + 1);
            _lowWindow = new Queue<double>(channelN + 1);
            _closeWindow = new Queue<double>(maN + 1);
            _atrTrueRanges = new Queue<double>(atrN + 1);
        }

        /// <summary>历史数据预热（只更新指标，不检查信号）</summary>
        public void Warmup(Bar bar) => Feed(bar);

        /// <summary>喂一根 Bar，更新所有指标</summary>
        public void Feed(Bar bar)
        {
            // 保存 MA 前值（用于判断趋势方向）
            PrevMA = CurrentMA;

            // ── ATR ──
            if (!double.IsNaN(_prevClose))
            {
                var tr = Math.Max(bar.HighDouble - bar.LowDouble,
                    Math.Max(Math.Abs(bar.HighDouble - _prevClose),
                             Math.Abs(bar.LowDouble - _prevClose)));
                _atrTrueRanges.Enqueue(tr);
                _atrSum += tr;
                if (_atrTrueRanges.Count > _atrN)
                    _atrSum -= _atrTrueRanges.Dequeue();
                if (_atrTrueRanges.Count >= _atrN)
                    CurrentAtr = _atrSum / _atrN;
            }
            _prevClose = bar.CloseDouble;

            // ── MA ──
            _closeWindow.Enqueue(bar.CloseDouble);
            _maSum += bar.CloseDouble;
            if (_closeWindow.Count > _maN)
                _maSum -= _closeWindow.Dequeue();
            if (_closeWindow.Count >= _maN)
                CurrentMA = _maSum / _maN;

            // ── Donchian 通道 ──
            _highWindow.Enqueue(bar.HighDouble);
            _lowWindow.Enqueue(bar.LowDouble);
            if (_highWindow.Count > _channelN) { _highWindow.Dequeue(); _lowWindow.Dequeue(); }
            if (_highWindow.Count >= _channelN)
            {
                ChannelHigh = _highWindow.Max();
                ChannelLow = _lowWindow.Min();
            }

            // ── 出场通道 ──
            if (_highWindow.Count >= _exitN)
            {
                // 使用更大的窗口数据计算出场边界
                var exitHighs = _highWindow.TakeLast(_exitN);
                var exitLows = _lowWindow.TakeLast(_exitN);
                ExitHigh = exitHighs.Any() ? exitHighs.Max() : bar.HighDouble;
                ExitLow = exitLows.Any() ? exitLows.Min() : bar.LowDouble;
            }
        }

        public void ResetTrade()
        {
            TrailingStop = 0;
            TakeProfit = 0;
            EntryPrice = 0;
            Direction = null;
            BarsInTrade = 0;
        }
    }
}
