using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
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
    [StrategyParameter(Description = "布林带周期", DefaultValue = 20, Min = 10, Max = 50, Category = "Entry")]
    public int Period { get; set; } = 20;

    [StrategyParameter(Description = "布林带标准差倍数", DefaultValue = 2.0, Min = 1.0, Max = 3.0, Category = "Entry")]
    public double StdDevMult { get; set; } = 2.0;

    [StrategyParameter(Description = "硬止损比例", DefaultValue = 0.015, Min = 0.005, Max = 0.05, Category = "Risk")]
    public double StopLossPct { get; set; } = 0.015;

    [StrategyParameter(Description = "止盈: 回到中轨(1.0=中轨, 0=关闭)", DefaultValue = 1.0, Min = 0, Max = 1.5, Category = "Risk")]
    public double TakeProfitBand { get; set; } = 1.0;

    [StrategyParameter(Description = "ATR周期(跟踪止损)", DefaultValue = 14, Min = 10, Max = 30, Category = "Risk")]
    public int AtrPeriod { get; set; } = 14;

    [StrategyParameter(Description = "跟踪止损ATR倍数", DefaultValue = 1.5, Min = 1.0, Max = 3.0, Category = "Risk")]
    public double TrailAtrMult { get; set; } = 1.5;

    [StrategyParameter(Description = "单笔风险占比", DefaultValue = 0.015, Min = 0.005, Max = 0.05, Category = "Position")]
    public double RiskPerTrade { get; set; } = 0.015;

    [StrategyParameter(Description = "最大持仓手数", DefaultValue = 2, Min = 1, Max = 10, Category = "Position")]
    public int MaxPosition { get; set; } = 2;

    [StrategyParameter(Description = "ADX上限(>此值不交易, 0=关闭)", DefaultValue = 25, Min = 0, Max = 60, Category = "Filter")]
    public int MaxAdx { get; set; } = 25;

    [StrategyParameter(Description = "ADX周期", DefaultValue = 14, Min = 7, Max = 30, Category = "Filter")]
    public int AdxPeriod { get; set; } = 14;

    [StrategyParameter(Description = "最低带宽%(布林带宽度/Mid, 0=关闭)", DefaultValue = 0.02, Min = 0, Max = 0.10, Category = "Filter")]
    public double MinBandWidth { get; set; } = 0.02;

    [StrategyParameter(Description = "入场冷却期(K线数)", DefaultValue = 10, Min = 0, Max = 50, Category = "Filter")]
    public int EntryCooldown { get; set; } = 10;

    [StrategyParameter(Description = "入场区域比例(0-0.5, 越小越严格)", DefaultValue = 0.20, Min = 0.05, Max = 0.50, Category = "Entry")]
    public double EntryZoneRatio { get; set; } = 0.20;

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
                var q = CalcLots(price, s, bar.InstrumentId);
                if (q > 0)
                {
                    _ctx.MarketBuy(bar.InstrumentId, q, $"超卖 BW={bandWidth*100:F1}%");
                    s.EntryPrice = price; s.TrailStop = price - TrailAtrMult * s.Atr;
                }
            }
            else if (price >= s.Upper - (s.Upper - s.MidBand) * zone)
            {
                var q = CalcLots(price, s, bar.InstrumentId);
                if (q > 0)
                {
                    _ctx.MarketSell(bar.InstrumentId, q, $"超买 BW={bandWidth*100:F1}%");
                    s.EntryPrice = price; s.TrailStop = price + TrailAtrMult * s.Atr;
                }
            }
        }
    }

    private int CalcLots(double price, InstrumentState s, string instId)
    {
        if (s.Atr <= 0) return 0;
        var f = _ctx.GetFuture(instId);
        var mult = (double)(f?.TradingUnit ?? 10m);
        var equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        var riskAmt = equity * RiskPerTrade;
        var stopDist = StopLossPct * price;
        int lots = Math.Max(1, (int)(riskAmt / (stopDist * mult)));
        var marginRate = (double)(f?.MarginRate ?? 0.08m);
        while (lots > 1 && price * mult * marginRate * lots > equity * 0.5) lots--;
        if (lots > MaxPosition) lots = MaxPosition;
        return lots;
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
