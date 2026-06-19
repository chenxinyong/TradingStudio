using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// 双均线趋势跟踪 — ATR动态仓位 + 追踪止损。
/// SMA金叉做多/死叉做空，反向穿越或追踪止损出场。
/// 仓位: 风险金额/(ATR止损距离×合约乘数)，受保证金上限25%约束。
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

    [StrategyParameter(Description = "单笔风险占比", DefaultValue = 0.02, Min = 0.005, Max = 0.05, Category = "Position")]
    public double RiskPerTrade { get; set; } = 0.02;

    [StrategyParameter(Description = "最大保证金占比", DefaultValue = 0.25, Min = 0.10, Max = 0.50, Category = "Position")]
    public double MaxMarginRatio { get; set; } = 0.25;

    public string Name => "双均线趋势跟踪(ATR风控)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, State> _state = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        foreach (var inst in context.SubscribedInstruments)
        {
            var history = context.GetBarHistory(inst);
            if (history.Count < SlowPeriod + AtrPeriod)
            {
                context.LogWarning($"{inst}: 历史不足 ({history.Count}<{SlowPeriod + AtrPeriod})");
                continue;
            }
            var s = new State(FastPeriod, SlowPeriod, AtrPeriod);
            foreach (var bar in history) s.Update(bar);
            _state[inst] = s;
            context.Log($"{inst}: Fast={s.FastMA:F2} Slow={s.SlowMA:F2} ATR={s.Atr:F2} ({history.Count} bars)");
        }
        _ctx.Log($"初始化: {Name} [{string.Join(", ", context.SubscribedInstruments)}]");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;
        if (_ctx.IsWarmup) { s.Update(bar); return; }

        var prevFast = s.FastMA;
        var prevSlow = s.SlowMA;
        s.Update(bar);

        var pos = _ctx.GetPosition(bar.InstrumentId);
        var hasLong = pos is not null && pos.Quantity > 0;
        var hasShort = pos is not null && pos.Quantity < 0;

        // ── 出场 ──
        if (hasLong)
        {
            var exit = false; var reason = "";
            if (s.FastMA < s.SlowMA && prevFast >= prevSlow) { exit = true; reason = "死叉平多"; }
            else if (bar.LowDouble <= s.Trail) { exit = true; reason = $"止损@{s.Trail:F1}"; }
            else { var t = bar.CloseDouble - StopAtrMult * s.Atr; if (t > s.Trail) s.Trail = t; }

            if (exit) { _ctx.ClosePosition(bar.InstrumentId); s.Trail = 0; }
        }
        else if (hasShort)
        {
            var exit = false; var reason = "";
            if (s.FastMA > s.SlowMA && prevFast <= prevSlow) { exit = true; reason = "金叉平空"; }
            else if (bar.HighDouble >= s.Trail) { exit = true; reason = $"止损@{s.Trail:F1}"; }
            else { var t = bar.CloseDouble + StopAtrMult * s.Atr; if (t < s.Trail) s.Trail = t; }

            if (exit) { _ctx.ClosePosition(bar.InstrumentId); s.Trail = 0; }
        }

        // ── 入场 ──
        if (!hasLong && !hasShort)
        {
            if (s.Atr / bar.CloseDouble < 0.003) return; // 波动率太低

            if (prevFast <= prevSlow && s.FastMA > s.SlowMA)
            {
                var q = CalcLots(bar.CloseDouble, s, bar.InstrumentId);
                if (q > 0) { _ctx.MarketBuy(bar.InstrumentId, q, "金叉"); s.Trail = bar.CloseDouble - StopAtrMult * s.Atr; }
            }
            else if (prevFast >= prevSlow && s.FastMA < s.SlowMA)
            {
                var q = CalcLots(bar.CloseDouble, s, bar.InstrumentId);
                if (q > 0) { _ctx.MarketSell(bar.InstrumentId, q, "死叉"); s.Trail = bar.CloseDouble + StopAtrMult * s.Atr; }
            }
        }
    }

    public void OnOrderEvent(OrderEvent evt) { }
    public void OnEndOfAlgorithm() { }

    /// <summary>ATR动态仓位: 风险金额/(止损距离×乘数)，多重约束</summary>
    private int CalcLots(double price, State s, string instId)
    {
        if (s.Atr <= 0) return 0;
        var f = _ctx.GetFuture(instId);
        var mult = (double)(f?.TradingUnit ?? 10m);
        var marginRate = (double)(f?.MarginRate ?? 0.08m);
        var equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        var contractValue = price * mult;

        // ① ATR/Price ≥ 0.5% — 波动率太低无法交易
        if (s.Atr / price < 0.005) return 0;

        // ② 止损距离 ≥ 0.5% 合约价值（防止止损太近导致过大仓位）
        var stopDist = StopAtrMult * s.Atr;
        var riskPerLot = stopDist * mult;
        if (riskPerLot < contractValue * 0.005) return 0;

        // ③ ATR仓位: riskAmt / riskPerLot
        var riskAmt = equity * RiskPerTrade;
        int lots = Math.Max(1, (int)(riskAmt / riskPerLot));

        // ④ 名义价值上限: ≤ 1× equity（防止杠杆失控）
        var maxByNotional = (int)(equity / contractValue);
        if (lots > maxByNotional) lots = maxByNotional;

        // ⑤ 保证金上限: ≤ MaxMarginRatio × equity
        var marginPerLot = contractValue * marginRate;
        while (lots > 0 && marginPerLot * lots > equity * MaxMarginRatio) lots--;

        // ⑥ 绝对上限 20 手
        if (lots > 20) lots = 20;

        return lots;
    }

    private class State
    {
        private readonly int _fn, _sn, _an;
        private readonly Queue<double> _fq, _sq, _trq;
        private double _fs, _ss, _ts, _prev = double.NaN;
        public double FastMA, SlowMA, Atr, Trail;

        public State(int fn, int sn, int an)
        { _fn = fn; _sn = sn; _an = an; _fq = new(fn + 1); _sq = new(sn + 1); _trq = new(an + 1); }

        public void Update(Bar bar)
        {
            var c = bar.CloseDouble;
            // Fast SMA
            _fq.Enqueue(c); _fs += c; if (_fq.Count > _fn) _fs -= _fq.Dequeue();
            if (_fq.Count >= _fn) FastMA = _fs / _fn;
            // Slow SMA
            _sq.Enqueue(c); _ss += c; if (_sq.Count > _sn) _ss -= _sq.Dequeue();
            if (_sq.Count >= _sn) SlowMA = _ss / _sn;
            // ATR
            if (!double.IsNaN(_prev))
            {
                var tr = Math.Max(bar.HighDouble - bar.LowDouble,
                    Math.Max(Math.Abs(bar.HighDouble - _prev), Math.Abs(bar.LowDouble - _prev)));
                _trq.Enqueue(tr); _ts += tr; if (_trq.Count > _an) _ts -= _trq.Dequeue();
                if (_trq.Count >= _an) Atr = _ts / _an;
            }
            _prev = c;
        }
    }
}
