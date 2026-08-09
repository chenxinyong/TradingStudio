using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Strategy;

/// <summary>
/// ATR通道突破趋势跟踪策略 — 多品种日内中频。
///
/// 核心逻辑:
///   多头: High突破N根Bar最高价(前值) + MA趋势向上 → 入场 (Market at Close)
///   空头: Low跌破N根Bar最低价(前值) + MA趋势向下 → 入场
///   止损: 2×ATR 跟踪止损
///   止盈: 3×ATR 目标价止盈
///   出场: 反向突破M根Bar边界 或 跟踪止损触发 或 止盈触发
///
/// 关键设计:
///   - 通道使用前值(不含本根Bar)避免"当前Bar包含自身"的数学不可行问题
///   - 过滤零成交量Bar(连续合约展期缺口标记)
///   - 支持重新入场冷却期，避免震荡市中反复止损
///
/// 参考: Richard Donchian通道 + Ed Seykota趋势跟踪 + ATR动态仓位
/// </summary>
public class DonchianTrendStrategy : IStrategy
{
    // ── 策略参数 (v2: StrategyParam<T>, 借鉴 StockSharp) ──

    public StrategyParam<int> ChannelPeriod { get; } = new("ChannelPeriod", 20)
        { Group = "Entry", Description = "通道周期(K线数)", OptimizeRange = (10, 60, 5) };

    public StrategyParam<int> ExitPeriod { get; } = new("ExitPeriod", 10)
        { Group = "Exit", Description = "出场通道周期(K线数)", OptimizeRange = (5, 30, 5) };

    public StrategyParam<int> TrendMAPeriod { get; } = new("TrendMAPeriod", 50)
        { Group = "Filter", Description = "趋势MA周期 (0=关闭)", OptimizeRange = (0, 200, 20) };

    public StrategyParam<int> AtrPeriod { get; } = new("AtrPeriod", 20)
        { Group = "Risk", Description = "ATR周期", OptimizeRange = (10, 40, 5) };

    public StrategyParam<double> StopAtrMult { get; } = new("StopAtrMult", 2.0)
        { Group = "Risk", Description = "止损ATR倍数", OptimizeRange = (1.0, 4.0, 0.5) };

    public StrategyParam<double> TakeProfitAtrMult { get; } = new("TakeProfitAtrMult", 3.0)
        { Group = "Risk", Description = "止盈ATR倍数 (0=关闭)", OptimizeRange = (0, 10.0, 1.0) };

    public StrategyParam<double> MinVolatility { get; } = new("MinVolatility", 0.003)
        { Group = "Filter", Description = "最低波动率(ATR/Close)", OptimizeRange = (0.001, 0.03, 0.002) };

    public StrategyParam<double> RiskPerTrade { get; } = new("RiskPerTrade", 0.02)
        { Group = "Position", Description = "单笔风险占比", OptimizeRange = (0.005, 0.05, 0.005) };

    public StrategyParam<int> MaxBarsInTrade { get; } = new("MaxBarsInTrade", 0)
        { Group = "Exit", Description = "最大持仓K线数(0=不限)", OptimizeRange = (0, 500, 50) };

    public StrategyParam<int> ReentryCooldown { get; } = new("ReentryCooldown", 5)
        { Group = "Entry", Description = "重新入场冷却期(K线数)", OptimizeRange = (0, 50, 5) };

    public string Name => "Donchian通道趋势跟踪(ATR动态仓位)";

    // ═══ 内部状态 ═══

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();
    private int _barCount;

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        var minNeeded = TrendMAPeriod + ChannelPeriod;

        foreach (var inst in context.SubscribedInstruments)
        {
            var history = context.GetBarHistory(inst);
            var state = new InstrumentState(inst, ChannelPeriod, ExitPeriod, TrendMAPeriod, AtrPeriod);
            _state[inst] = state;

            // 预热：用历史数据初始化指标（跳过零成交量 Bar）
            if (history.Count > 0)
            {
                foreach (var bar in history)
                {
                    if (bar.Volume > 0)
                        state.Warmup(bar);
                }
            }

            if (history.Count < minNeeded)
                context.Log($"{inst}: 启动预热 (历史={history.Count}根, 需要={minNeeded}根, 将在交易中自然积累)");
            else
                context.Log($"{inst}: MA={state.CurrentMA:F2} ATR={state.CurrentAtr:F4} " +
                           $"ChHi={state.ChannelHigh:F2} ChLo={state.ChannelLow:F2} ({history.Count} bars)");
        }

        _ctx.Log($"初始化: {Name} on [{string.Join(", ", context.SubscribedInstruments)}]");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        // ── 过滤零成交量 Bar（连续合约展期缺口标记） ──
        if (bar.Volume <= 0) return;

        // ── 懒初始化：应对 Initialize 时无历史数据的情况 ──
        if (!_state.TryGetValue(bar.InstrumentId, out var s))
        {
            s = new InstrumentState(bar.InstrumentId, ChannelPeriod, ExitPeriod, TrendMAPeriod, AtrPeriod);
            _state[bar.InstrumentId] = s;
        }

        // 预热：历史和实时 feed 共享 Feed 方法
        if (_ctx.IsWarmup) { s.Feed(bar); return; }

        // ── 快照当前 Bar 之前的指标值（用于信号判断，避免"当前 Bar 包含自身"问题） ──
        var prevChHi = s.ChannelHigh;
        var prevChLo = s.ChannelLow;
        var prevExitHi = s.ExitHigh;
        var prevExitLo = s.ExitLow;
        var prevMA = s.CurrentMA;
        var prevPrevMA = s.PrevMA;
        var prevAtr = s.CurrentAtr;

        // 更新指标（包含本根 Bar）
        s.Feed(bar);
        _barCount++;

        // ── 指标就绪检查（TrendMAPeriod=0 时跳过 MA 检查） ──
        var maReady = TrendMAPeriod == 0 || s._maReady >= TrendMAPeriod;
        var atrReady = s._atrReady >= AtrPeriod;
        var chReady = s._chReady >= ChannelPeriod;
        if (!(maReady && atrReady && chReady)) return;

        // ── 波动率过滤 ──
        var volatility = prevAtr / bar.CloseDouble;
        if (volatility < MinVolatility) return;

        var pos = _ctx.GetPosition(s.InstrumentId);
        var hasPosition = pos is not null && pos.Quantity != 0;

        // ── 出场逻辑 ──
        if (hasPosition)
        {
            var shouldExit = false;
            var exitReason = "";

            // ① 止盈 (优先检查)
            if (!shouldExit && TakeProfitAtrMult > 0 && s.TakeProfit > 0)
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

            // ② 反向突破出场
            if (!shouldExit && pos!.Quantity > 0 && bar.CloseDouble < prevExitLo)
            {
                shouldExit = true;
                exitReason = $"反向突破出场(多头) {bar.CloseDouble:F0}<{prevExitLo:F0}";
            }
            else if (!shouldExit && pos.Quantity < 0 && bar.CloseDouble > prevExitHi)
            {
                shouldExit = true;
                exitReason = $"反向突破出场(空头) {bar.CloseDouble:F0}>{prevExitHi:F0}";
            }
            // ③ 跟踪止损
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
            // ④ 时间出场
            else if (!shouldExit && MaxBarsInTrade > 0 && s.BarsInTrade >= MaxBarsInTrade)
            {
                shouldExit = true;
                exitReason = $"持仓超时 ({s.BarsInTrade}bars)";
            }

            if (shouldExit)
            {
                var slReason = exitReason.Contains("止损") ? "SL"
                    : exitReason.Contains("突破") ? "Signal"
                    : exitReason.Contains("超时") ? "Timeout"
                    : "Signal";
                _ctx.ClosePosition(s.InstrumentId, slReason);
                _ctx.Log($"出场: {s.InstrumentId} {exitReason} @ {bar.CloseDouble:F2}");
                s.ResetTrade();
                s.CooldownRemaining = ReentryCooldown;
                return;
            }

            // 更新跟踪止损 (使用含本Bar的ATR)
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

        // ── 冷却期递减 ──
        if (!hasPosition && s.CooldownRemaining > 0)
        {
            s.CooldownRemaining--;
            return;
        }

        // ── 入场逻辑 ──
        if (!hasPosition)
        {
            var trendUp = TrendMAPeriod == 0 || s.CurrentMA > s.PrevMA;
            var trendDown = TrendMAPeriod == 0 || s.CurrentMA < s.PrevMA;

            // 多头: High突破 + MA向上
            if (bar.HighDouble > prevChHi && trendUp)
            {
                var qty = PositionSizer.FromAtrStop(bar.CloseDouble, s.CurrentAtr, StopAtrMult,
                    _ctx.GetFuture(s.InstrumentId), (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
                    RiskPerTrade, 0.5);
                if (qty > 0)
                {
                    _ctx.MarketBuy(s.InstrumentId, qty, $"突破入场: H{bar.HighDouble:F0}>{prevChHi:F0}");
                    s.TrailingStop = bar.CloseDouble - StopAtrMult * s.CurrentAtr;
                    s.TakeProfit = TakeProfitAtrMult > 0 ? bar.CloseDouble + TakeProfitAtrMult * s.CurrentAtr : 0;
                    s.EntryPrice = bar.CloseDouble; s.Direction = "Long"; s.BarsInTrade = 0;
                    _ctx.Log($"多头入场: {s.InstrumentId} @{bar.CloseDouble:F0} " +
                             $"SL={s.TrailingStop:F0} Qty={qty}");
                }
            }
            // 空头: Low跌破 + MA向下
            else if (bar.LowDouble < prevChLo && trendDown)
            {
                var qty = PositionSizer.FromAtrStop(bar.CloseDouble, s.CurrentAtr, StopAtrMult,
                    _ctx.GetFuture(s.InstrumentId), (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
                    RiskPerTrade, 0.5);
                if (qty > 0)
                {
                    _ctx.MarketSell(s.InstrumentId, qty, $"突破入场: L{bar.LowDouble:F0}<{prevChLo:F0}");
                    s.TrailingStop = bar.CloseDouble + StopAtrMult * s.CurrentAtr;
                    s.TakeProfit = TakeProfitAtrMult > 0 ? bar.CloseDouble - TakeProfitAtrMult * s.CurrentAtr : 0;
                    s.EntryPrice = bar.CloseDouble; s.Direction = "Short"; s.BarsInTrade = 0;
                    _ctx.Log($"空头入场: {s.InstrumentId} @{bar.CloseDouble:F0} " +
                             $"SL={s.TrailingStop:F0} Qty={qty}");
                }
            }
        }

        // ── 阶段性汇总 ──
        if (_barCount % 1000 == 0)
        {
            _ctx.Log($"阶段汇总 [{_barCount}]: " +
                string.Join(" | ", _state.Values.Select(st =>
                    $"{st.InstrumentId} ready={st.IsReady}")));
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
        public double PrevChannelHigh { get; private set; }
        public double PrevChannelLow { get; private set; }
        public double ExitHigh { get; private set; }
        public double ExitLow { get; private set; }
        public double AvgVolume;  // 简单成交量均线

        // 指标就绪状态
        public int _maReady, _atrReady, _chReady;
        public bool IsReady => _maReady >= _maN && _atrReady >= _atrN && _chReady >= _channelN;

        // 交易状态
        public double TrailingStop;
        public double TakeProfit;
        public double EntryPrice;
        public string? Direction;
        public int BarsInTrade;
        public int CooldownRemaining;

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

        public void Warmup(Bar bar) => Feed(bar);

        public void Feed(Bar bar)
        {
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
                _atrReady = _atrTrueRanges.Count;
                if (_atrReady >= _atrN)
                    CurrentAtr = _atrSum / _atrN;
            }
            _prevClose = bar.CloseDouble;

            // ── MA ──
            _closeWindow.Enqueue(bar.CloseDouble);
            _maSum += bar.CloseDouble;
            if (_closeWindow.Count > _maN)
                _maSum -= _closeWindow.Dequeue();
            _maReady = _closeWindow.Count;
            if (_maReady >= _maN)
                CurrentMA = _maSum / _maN;

            // ── Donchian 通道 ──
            // 保存前值（用于波动率收缩判断）
            PrevChannelHigh = ChannelHigh;
            PrevChannelLow = ChannelLow;

            _highWindow.Enqueue(bar.HighDouble);
            _lowWindow.Enqueue(bar.LowDouble);
            if (_highWindow.Count > _channelN) { _highWindow.Dequeue(); _lowWindow.Dequeue(); }
            _chReady = _highWindow.Count;
            if (_chReady >= _channelN)
            {
                ChannelHigh = _highWindow.Max();
                ChannelLow = _lowWindow.Min();
            }

            // ── 成交量均线 (EMA风格简化) ──
            if (AvgVolume <= 0)
                AvgVolume = bar.Volume;
            else
                AvgVolume = AvgVolume * 0.95 + bar.Volume * 0.05;

            // ── 出场通道 ──
            if (_chReady >= _exitN)
            {
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
