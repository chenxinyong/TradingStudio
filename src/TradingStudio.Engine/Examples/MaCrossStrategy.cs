using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
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

    [StrategyParameter(Description = "单笔风险占比", DefaultValue = 0.02, Min = 0.005, Max = 0.05, Category = "Position")]
    public double RiskPerTrade { get; set; } = 0.02;

    [StrategyParameter(Description = "最大保证金占比", DefaultValue = 0.25, Min = 0.10, Max = 0.50, Category = "Position")]
    public double MaxMarginRatio { get; set; } = 0.25;

    [StrategyParameter(Description = "最大持仓手数", DefaultValue = 2, Min = 1, Max = 20, Category = "Position")]
    public int MaxPosition { get; set; } = 2;

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
            {
                context.LogWarning($"{inst}: 历史不足 ({history.Count}<{SlowPeriod + AtrPeriod})");
                continue;
            }

            // 注册 SMA 指标到 IndicatorManager（多策略共享去重）
            var fastTag = FastPeriod.ToString();
            var slowTag = SlowPeriod.ToString();
            var fastSma = context.RegisterIndicator(inst, new SmaIndicator(FastPeriod), fastTag);
            var slowSma = context.RegisterIndicator(inst, new SmaIndicator(SlowPeriod), slowTag);
            _fastSmas[inst] = fastSma;
            _slowSmas[inst] = slowSma;

            // 预热：手动 Feed 指标 + ATR
            var s = new InstrumentState(AtrPeriod);
            foreach (var bar in history)
            {
                fastSma.Update(bar);
                slowSma.Update(bar);
                s.UpdateAtr(bar);
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

        // 预热期：手动 Feed 指标（IndicatorManager.Feed 在实盘主循环才调用）
        if (_ctx.IsWarmup)
        {
            _fastSmas[bar.InstrumentId].Update(bar);
            _slowSmas[bar.InstrumentId].Update(bar);
            s.UpdateAtr(bar);
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

        // Feed ATR（策略内自算）
        s.UpdateAtr(bar);

        var pos = _ctx.GetPosition(bar.InstrumentId);
        var hasLong = pos is not null && pos.Quantity > 0;
        var hasShort = pos is not null && pos.Quantity < 0;

        // ── 出场 + 反手 ──
        if (hasLong)
        {
            var exit = false; var reverse = false;
            if (curFast < curSlow && prevFast >= prevSlow)
                { exit = true; reverse = true; }                                  // 死叉 → 平多反手做空
            else if (bar.LowDouble <= s.Trail)
                { exit = true; reverse = curFast < curSlow; }                     // 止损 → MA已转空才反手
            else
                { var t = bar.CloseDouble - StopAtrMult * s.Atr; if (t > s.Trail) s.Trail = t; }

            if (exit)
            {
                _ctx.ClosePosition(bar.InstrumentId); s.Trail = 0;
                if (reverse)
                {
                    var q = CalcLots(bar.CloseDouble, s, bar.InstrumentId);
                    if (q > 0) { _ctx.MarketSell(bar.InstrumentId, q, "反手"); s.Trail = bar.CloseDouble + StopAtrMult * s.Atr; }
                }
            }
        }
        else if (hasShort)
        {
            var exit = false; var reverse = false;
            if (curFast > curSlow && prevFast <= prevSlow)
                { exit = true; reverse = true; }                                  // 金叉 → 平空反手做多
            else if (bar.HighDouble >= s.Trail)
                { exit = true; reverse = curFast > curSlow; }                     // 止损 → MA已转多才反手
            else
                { var t = bar.CloseDouble + StopAtrMult * s.Atr; if (t < s.Trail) s.Trail = t; }

            if (exit)
            {
                _ctx.ClosePosition(bar.InstrumentId); s.Trail = 0;
                if (reverse)
                {
                    var q = CalcLots(bar.CloseDouble, s, bar.InstrumentId);
                    if (q > 0) { _ctx.MarketBuy(bar.InstrumentId, q, "反手"); s.Trail = bar.CloseDouble - StopAtrMult * s.Atr; }
                }
            }
        }

        // ── 入场（仅首次开仓，反手已在出场段处理）──
        if (!hasLong && !hasShort)
        {
            if (s.Atr / bar.CloseDouble < 0.003) return;

            if (prevFast <= prevSlow && curFast > curSlow)
            {
                var q = CalcLots(bar.CloseDouble, s, bar.InstrumentId);
                if (q > 0) { _ctx.MarketBuy(bar.InstrumentId, q, "金叉"); s.Trail = bar.CloseDouble - StopAtrMult * s.Atr; }
            }
            else if (prevFast >= prevSlow && curFast < curSlow)
            {
                var q = CalcLots(bar.CloseDouble, s, bar.InstrumentId);
                if (q > 0) { _ctx.MarketSell(bar.InstrumentId, q, "死叉"); s.Trail = bar.CloseDouble + StopAtrMult * s.Atr; }
            }
        }

        // 保存当前 SMA 值作为下一根 Bar 的"前值"（穿越检测用）
        s.PrevFast = curFast;
        s.PrevSlow = curSlow;
    }

    public void OnOrderEvent(OrderEvent evt) { }
    public void OnEndOfAlgorithm() { }

    private int CalcLots(double price, InstrumentState s, string instId)
    {
        if (s.Atr <= 0) return 0;
        var f = _ctx.GetFuture(instId);
        var mult = (double)(f?.TradingUnit ?? 10m);
        var marginRate = (double)(f?.MarginRate ?? 0.08m);
        var equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        var contractValue = price * mult;

        if (s.Atr / price < 0.005) return 0;

        var stopDist = StopAtrMult * s.Atr;
        var riskPerLot = stopDist * mult;
        if (riskPerLot < contractValue * 0.005) return 0;

        var riskAmt = equity * RiskPerTrade;
        int lots = Math.Max(1, (int)(riskAmt / riskPerLot));

        var maxByNotional = (int)(equity / contractValue);
        if (lots > maxByNotional) lots = maxByNotional;

        var marginPerLot = contractValue * marginRate;
        while (lots > 0 && marginPerLot * lots > equity * MaxMarginRatio) lots--;

        if (lots > 20) lots = 20;
        if (lots > MaxPosition) lots = MaxPosition;

        return lots;
    }

    /// <summary>品种状态 — ATR自算 + 追踪止损位 + 前值快照</summary>
    private class InstrumentState
    {
        private readonly int _an;
        private readonly Queue<double> _trq;
        private double _ts, _prev = double.NaN;
        public double Atr, Trail;
        public double PrevFast = double.NaN, PrevSlow = double.NaN;  // 上一根 Bar 的 SMA 值（穿越检测用）

        public InstrumentState(int atrPeriod)
        { _an = atrPeriod; _trq = new(atrPeriod + 1); }

        public void UpdateAtr(Bar bar)
        {
            if (!double.IsNaN(_prev))
            {
                var tr = Math.Max(bar.HighDouble - bar.LowDouble,
                    Math.Max(Math.Abs(bar.HighDouble - _prev), Math.Abs(bar.LowDouble - _prev)));
                _trq.Enqueue(tr); _ts += tr;
                if (_trq.Count > _an) _ts -= _trq.Dequeue();
                if (_trq.Count >= _an) Atr = _ts / _an;
            }
            _prev = bar.CloseDouble;
        }
    }
}
