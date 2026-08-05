using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// 布林带均值回归策略 — 价格触及上下轨时反向交易。
///
/// 核心逻辑:
///   多头: 价格跌破下轨 + 回到下轨上方 → 入场做多 (超卖反弹)
///   空头: 价格突破上轨 + 回到上轨下方 → 入场做空 (超买回落)
///   止损: 固定百分比 + ATR 跟踪止损
///   止盈: 价格回到中轨 (均值回归目标)
///
/// 与趋势跟踪互补: 震荡市中表现更好。
/// </summary>
public class BollingerReversionStrategy : IStrategy
{
    // ── 策略参数 (v2: StrategyParam<T>, 借鉴 StockSharp) ──

    public StrategyParam<int> Period { get; } = new("Period", 20)
        { Group = "Entry", Description = "布林带周期", OptimizeRange = (10, 50, 5) };

    public StrategyParam<double> StdDevMult { get; } = new("StdDevMult", 2.0)
        { Group = "Entry", Description = "布林带标准差倍数", OptimizeRange = (1.0, 3.0, 0.5) };

    public StrategyParam<double> StopLossPct { get; } = new("StopLossPct", 0.015)
        { Group = "Risk", Description = "硬止损比例", OptimizeRange = (0.005, 0.05, 0.005) };

    public StrategyParam<double> TakeProfitBand { get; } = new("TakeProfitBand", 1.0)
        { Group = "Risk", Description = "止盈: 回到中轨(1.0=中轨, 0=关闭)", OptimizeRange = (0, 1.5, 0.25) };

    public StrategyParam<int> AtrPeriod { get; } = new("AtrPeriod", 14)
        { Group = "Risk", Description = "ATR周期(跟踪止损)", OptimizeRange = (10, 30, 5) };

    public StrategyParam<double> TrailAtrMult { get; } = new("TrailAtrMult", 1.5)
        { Group = "Risk", Description = "跟踪止损ATR倍数", OptimizeRange = (1.0, 3.0, 0.5) };

    public StrategyParam<double> RiskPerTrade { get; } = new("RiskPerTrade", 0.015)
        { Group = "Position", Description = "单笔风险占比", OptimizeRange = (0.005, 0.05, 0.005) };

    public StrategyParam<int> MaxPosition { get; } = new("MaxPosition", 2)
        { Group = "Position", Description = "最大持仓手数", OptimizeRange = (1, 10, 1) };

    public StrategyParam<int> MaxAdx { get; } = new("MaxAdx", 25)
        { Group = "Filter", Description = "ADX上限(>此值不交易)", OptimizeRange = (0, 60, 10) };

    public StrategyParam<int> AdxPeriod { get; } = new("AdxPeriod", 14)
        { Group = "Filter", Description = "ADX周期", OptimizeRange = (7, 30, 3) };

    public StrategyParam<double> MinBandWidth { get; } = new("MinBandWidth", 0.02)
        { Group = "Filter", Description = "最低带宽%(布林带宽度/Mid)", OptimizeRange = (0, 0.10, 0.01) };

    public StrategyParam<int> EntryCooldown { get; } = new("EntryCooldown", 10)
        { Group = "Filter", Description = "入场冷却期(K线数)", OptimizeRange = (0, 50, 5) };

    public StrategyParam<double> EntryZoneRatio { get; } = new("EntryZoneRatio", 0.20)
        { Group = "Entry", Description = "入场区域比例(越小越严格)", OptimizeRange = (0.05, 0.50, 0.05) };

    public string Name => "布林带均值回归";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        foreach (var inst in context.SubscribedInstruments)
        {
            var history = context.GetBarHistory(inst);
            if (history.Count < Period + AtrPeriod)
            {
                context.LogWarning($"{inst}: 历史不足 ({history.Count}<{Period + AtrPeriod})");
                continue;
            }

            var s = new InstrumentState(Period, AtrPeriod, AdxPeriod, StdDevMult);
            foreach (var bar in history) s.Feed(bar);
            _state[inst] = s;

            context.Log($"{inst}: Mid={s.MidBand:F2} Up={s.Upper:F2} Lo={s.Lower:F2} ATR={s.Atr:F2} ADX={s.Adx:F1}");
        }
        _ctx.Log($"初始化: {Name} [{string.Join(", ", context.SubscribedInstruments)}] " +
                 $"Period={Period} Std={StdDevMult} SL={StopLossPct:P1} TP_Band={TakeProfitBand:F1} MaxAdx={MaxAdx}");
    }

    public void OnTick(TickRecord tick, string id) { }
    public void OnOrderEvent(OrderEvent evt) { }
    public void OnEndOfAlgorithm() { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;
        if (_ctx.IsWarmup) { s.Feed(bar); return; }

        s.Feed(bar);

        // 趋势太强 / 带宽太窄 / 未就绪
        if (MaxAdx > 0 && s.Adx > MaxAdx) return;
        if (!s.IsReady) return;
        s.Cooldown--;
        var bandWidth = s.MidBand > 0 ? (s.Upper - s.Lower) / s.MidBand : 0;

        var price = bar.CloseDouble;
        var pos = _ctx.GetPosition(bar.InstrumentId);
        var hasLong = pos is not null && pos.Quantity > 0;
        var hasShort = pos is not null && pos.Quantity < 0;

        // ── 出场 ──
        if (hasLong)
        {
            var exit = false; string reason = "";
            // 止盈: 回到中轨
            if (TakeProfitBand > 0 && price >= s.MidBand * TakeProfitBand)
            { exit = true; reason = $"止盈@中轨 {price:F0}>{s.MidBand*TakeProfitBand:F0}"; }
            // 硬止损
            else if (price <= s.EntryPrice * (1 - StopLossPct))
            { exit = true; reason = $"止损 -{StopLossPct:P0}"; }
            // 跟踪止损
            else if (bar.LowDouble <= s.TrailStop)
            { exit = true; reason = $"跟踪止损@{s.TrailStop:F0}"; }
            // 回到上轨上方 (趋势反转)
            else if (price > s.Upper)
            { exit = true; reason = $"突破上轨 {price:F0}>{s.Upper:F0}"; }
            else
            { var t = price - TrailAtrMult * s.Atr; if (t > s.TrailStop) s.TrailStop = t; }

            if (exit) { _ctx.ClosePosition(bar.InstrumentId); s.ResetTrade(); s.Cooldown = EntryCooldown; }
        }
        else if (hasShort)
        {
            var exit = false; string reason = "";
            if (TakeProfitBand > 0 && price <= s.MidBand * (2 - TakeProfitBand))
            { exit = true; reason = $"止盈@中轨 {price:F0}<{s.MidBand*(2-TakeProfitBand):F0}"; }
            else if (price >= s.EntryPrice * (1 + StopLossPct))
            { exit = true; reason = $"止损 +{StopLossPct:P0}"; }
            else if (bar.HighDouble >= s.TrailStop)
            { exit = true; reason = $"跟踪止损@{s.TrailStop:F0}"; }
            else if (price < s.Lower)
            { exit = true; reason = $"跌破下轨 {price:F0}<{s.Lower:F0}"; }
            else
            { var t = price + TrailAtrMult * s.Atr; if (t < s.TrailStop) s.TrailStop = t; }

            if (exit) { _ctx.ClosePosition(bar.InstrumentId); s.ResetTrade(); s.Cooldown = EntryCooldown; }
        }

        // ── 入场 (带带宽过滤+冷却+收紧入场区) ──
        if (!hasLong && !hasShort && s.Cooldown <= 0 && bandWidth >= MinBandWidth)
        {
            var zone = EntryZoneRatio;
            var lowerZone = s.Lower + (s.MidBand - s.Lower) * zone;
            if (price <= lowerZone)
            {
                var q = PositionSizer.FromPctStop(price, StopLossPct,
                    _ctx.GetFuture(bar.InstrumentId), (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
                    RiskPerTrade, 0.5, MaxPosition);
                if (q > 0)
                {
                    _ctx.MarketBuy(bar.InstrumentId, q, $"超卖 BW={bandWidth*100:F1}%");
                    s.EntryPrice = price; s.TrailStop = price - TrailAtrMult * s.Atr;
                }
            }
            else if (price >= s.Upper - (s.Upper - s.MidBand) * zone)
            {
                var q = PositionSizer.FromPctStop(price, StopLossPct,
                    _ctx.GetFuture(bar.InstrumentId), (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
                    RiskPerTrade, 0.5, MaxPosition);
                if (q > 0)
                {
                    _ctx.MarketSell(bar.InstrumentId, q, $"超买 BW={bandWidth*100:F1}%");
                    s.EntryPrice = price; s.TrailStop = price + TrailAtrMult * s.Atr;
                }
            }
        }
    }

    private class InstrumentState
    {
        private readonly int _n, _an, _adxn;
        private readonly double _stdMult;
        private readonly Queue<double> _prices, _trVals;
        private readonly List<double> _dmPlus, _dmMinus, _trAdx;
        private double _prevHigh, _prevLow, _prevCloseAdx;

        public double MidBand, Upper, Lower, Atr;
        public double Adx, PrevClose;
        public double EntryPrice, TrailStop;
        public int Cooldown;
        public bool IsReady;

        public InstrumentState(int period, int atrPeriod, int adxPeriod, double stdMult)
        {
            _n = period; _an = atrPeriod; _adxn = adxPeriod; _stdMult = stdMult;
            _prices = new(period); _trVals = new(atrPeriod);
            _dmPlus = new(adxPeriod); _dmMinus = new(adxPeriod); _trAdx = new(adxPeriod);
        }

        public void Feed(Bar bar)
        {
            PrevClose = bar.CloseDouble;

            // 布林带
            _prices.Enqueue(bar.CloseDouble);
            if (_prices.Count > _n) _prices.Dequeue();
            if (_prices.Count >= _n)
            {
                double sum = 0, sumSq = 0;
                foreach (var p in _prices) { sum += p; sumSq += p * p; }
                MidBand = sum / _n;
                var std = Math.Sqrt(sumSq / _n - MidBand * MidBand);
                Upper = MidBand + _stdMult * std;
                Lower = MidBand - _stdMult * std;
            }

            // ATR (Wilder)
            if (!double.IsNaN(_prevHigh))
            {
                var tr = Math.Max(bar.HighDouble - bar.LowDouble,
                    Math.Max(Math.Abs(bar.HighDouble - PrevClose), Math.Abs(bar.LowDouble - PrevClose)));
                _trVals.Enqueue(tr);
                if (_trVals.Count > _an) _trVals.Dequeue();
                Atr = _trVals.Average();
            }

            // ADX (simplified)
            if (!double.IsNaN(_prevHigh))
            {
                var upMove = bar.HighDouble - _prevHigh;
                var downMove = _prevLow - bar.LowDouble;
                double plusDM = (upMove > downMove && upMove > 0) ? upMove : 0;
                double minusDM = (downMove > upMove && downMove > 0) ? downMove : 0;
                var trAdx = Math.Max(bar.HighDouble - bar.LowDouble,
                    Math.Max(Math.Abs(bar.HighDouble - _prevCloseAdx), Math.Abs(bar.LowDouble - _prevCloseAdx)));
                _dmPlus.Add(plusDM); _dmMinus.Add(minusDM); _trAdx.Add(trAdx);
                if (_dmPlus.Count > _adxn) { _dmPlus.RemoveAt(0); _dmMinus.RemoveAt(0); _trAdx.RemoveAt(0); }
                if (_dmPlus.Count >= _adxn && _trAdx.Sum() > 0)
                {
                    var diPlus = _dmPlus.Sum() / _trAdx.Sum() * 100;
                    var diMinus = _dmMinus.Sum() / _trAdx.Sum() * 100;
                    var dx = Math.Abs(diPlus - diMinus) / (diPlus + diMinus) * 100;
                    Adx = dx; // simplified: not smoothed
                }
            }
            _prevHigh = bar.HighDouble; _prevLow = bar.LowDouble;
            _prevCloseAdx = bar.CloseDouble;

            IsReady = _prices.Count >= _n && _trVals.Count >= _an;
        }

        public void ResetTrade() { EntryPrice = 0; TrailStop = 0; }
    }
}
