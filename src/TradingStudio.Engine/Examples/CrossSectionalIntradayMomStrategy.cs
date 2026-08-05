using TradingStudio.Core.Engine;
using TradingStudio.Core.Factors;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// 日内动量因子 — 横截面多品种策略 (v2: C# Factor 直连，无 Python CSV 依赖)。
///
/// 核心逻辑:
///   IntradayMomFactor 在每个品种上独立运行，实时计算 IntradayMom Z-score。
///   每天收盘前按 Z-score 排名，做多 TopN，做空 BottomN。
///   因子内置 Z-score 标准化工序（滑动窗口均值/std），策略无需自行计算。
///
/// Python验证: IntradayMom IC_IR=+0.96, OOS IC_IR=+1.05
/// </summary>
public class CrossSectionalIntradayMomStrategy : IStrategy
{
    // ── 策略参数 ──

    public StrategyParam<int> TopN { get; } = new("TopN", 5)
        { Group = "Portfolio", Description = "做多/做空品种数", OptimizeRange = (1, 20, 1) };

    public StrategyParam<int> FactorWindow { get; } = new("FactorWindow", 252)
        { Group = "Entry", Description = "因子滚动窗口天数", OptimizeRange = (60, 504, 20) };

    public StrategyParam<int> BarPeriodMinutes { get; } = new("BarPeriodMinutes", 5)
        { Group = "Data", Description = "Bar周期(分钟), 用于因子判断30min触发" };

    public StrategyParam<int> AtrPeriod { get; } = new("AtrPeriod", 20)
        { Group = "Risk", Description = "ATR周期", OptimizeRange = (10, 40, 5) };

    public StrategyParam<double> StopAtrMult { get; } = new("StopAtrMult", 2.0)
        { Group = "Risk", Description = "止损ATR倍数", OptimizeRange = (1.0, 5.0, 0.5) };

    public StrategyParam<double> TakeProfitAtrMult { get; } = new("TakeProfitAtrMult", 3.0)
        { Group = "Risk", Description = "止盈ATR倍数 (0=关闭)", OptimizeRange = (0, 10.0, 0.5) };

    public StrategyParam<double> RiskPerTrade { get; } = new("RiskPerTrade", 0.02)
        { Group = "Position", Description = "单品种单笔风险占比", OptimizeRange = (0.005, 0.05, 0.005) };

    public StrategyParam<double> MaxMarginRatio { get; } = new("MaxMarginRatio", 0.50)
        { Group = "Position", Description = "最大保证金占比", OptimizeRange = (0.20, 0.80, 0.05) };

    public StrategyParam<int> MaxPosition { get; } = new("MaxPosition", 2)
        { Group = "Position", Description = "单品种最大手数", OptimizeRange = (1, 10, 1) };

    public string Name => "横截面日内动量(TopN)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();

    // ── v2: C# Factor 直连，替代 Python CSV ──
    private readonly Dictionary<string, IntradayMomFactor> _factors = new();

    // 日切跟踪
    private DateOnly _lastTradingDay;
    private bool _firstDay = true;

    public void Initialize(StrategyContext context)
    {
        _ctx = context;

        foreach (var inst in context.SubscribedInstruments)
        {
            var factor = new IntradayMomFactor(FactorWindow);
            var s = new InstrumentState(AtrPeriod);

            // 预热: 遍历历史 Bar, 喂给因子
            var history = context.GetBarHistory(inst);
            DateOnly? prevDay = null;
            foreach (var bar in history)
            {
                s.UpdateAtr(bar);
                var isNewDay = prevDay.HasValue && bar.TradingDay != prevDay.Value;
                factor.Update(bar.CloseDouble, bar.BarTime, isNewDay, BarPeriodMinutes);
                prevDay = bar.TradingDay;
            }

            _factors[inst] = factor;
            _state[inst] = s;
        }
        context.Log($"v2 初始化: {_state.Count} 品种, 因子窗口={FactorWindow}, TopN={TopN}, BarPeriod={BarPeriodMinutes}min");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;
        if (!_factors.TryGetValue(bar.InstrumentId, out var factor)) return;

        s.UpdateAtr(bar);

        // ── Feed 因子 (实时计算 Z-score) ──
        var isNewDay = bar.TradingDay != _lastTradingDay;
        factor.Update(bar.CloseDouble, bar.BarTime, isNewDay, BarPeriodMinutes);

        if (_ctx.IsWarmup)
            return;

        // ── 日切检测：新交易日 → 平昨仓 + 开今仓 ──
        if (isNewDay)
        {
            if (!_firstDay)
            {
                CloseAllPositions();
                RebalancePortfolio();
            }
            _firstDay = false;
            _lastTradingDay = bar.TradingDay;
        }

        // ── 止损/止盈检查 ──
        var pos = _ctx.GetPosition(bar.InstrumentId);
        if (pos is not null && pos.Quantity != 0 && s.EntryPrice > 0)
        {
            bool shouldExit = false;
            string reason = "";

            if (pos.Quantity > 0) // Long
            {
                if (bar.LowDouble <= s.StopLoss)
                    { shouldExit = true; reason = "SL"; }
                else if (s.TakeProfit > 0 && bar.HighDouble >= s.TakeProfit)
                    { shouldExit = true; reason = "TP"; }
            }
            else // Short
            {
                if (bar.HighDouble >= s.StopLoss)
                    { shouldExit = true; reason = "SL"; }
                else if (s.TakeProfit > 0 && bar.LowDouble <= s.TakeProfit)
                    { shouldExit = true; reason = "TP"; }
            }

            if (shouldExit)
            {
                _ctx.ClosePosition(bar.InstrumentId);
                _ctx.Log($"止损/盈: {bar.InstrumentId} {reason} @{bar.CloseDouble:F4}");
                s.ResetTrade();
            }
        }
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Rejected && _state.TryGetValue(evt.InstrumentId, out var s))
        {
            s.ResetTrade();
            _ctx.Log($"订单被拒: {evt.InstrumentId} #{evt.OrderId}");
        }
    }

    public void OnEndOfAlgorithm() { }

    // ═══════════════════════════════════════════════════════════════
    // 核心调仓逻辑
    // ═══════════════════════════════════════════════════════════════

    private void CloseAllPositions()
    {
        foreach (var pos in _ctx.Positions)
        {
            if (pos.Quantity != 0)
            {
                _ctx.ClosePosition(pos.InstrumentId);
                if (_state.TryGetValue(pos.InstrumentId, out var s))
                    s.ResetTrade();
            }
        }
    }

    private void RebalancePortfolio()
    {
        // 从各品种因子获取最新 Z-score，排名
        var rankings = new List<(string Inst, double Z, double Atr, double Price)>();
        foreach (var (inst, s) in _state)
        {
            if (!_factors.TryGetValue(inst, out var factor) || !factor.IsReady) continue;
            if (s.Atr <= 0) continue;

            var z = factor.LastValue.ZScore;
            rankings.Add((inst, z, s.Atr, s.LastPrice));
        }

        if (rankings.Count < TopN * 2) return;

        // 按 Z-score 降序排列
        rankings.Sort((a, b) => b.Z.CompareTo(a.Z));

        var equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        int opened = 0;

        // 做多 TopN (Z-score 最高)
        for (int i = 0; i < Math.Min(TopN, rankings.Count); i++)
        {
            var (inst, z, atr, price) = rankings[i];
            if (atr / price < 0.001) continue; // 波动率过低

            var future = _ctx.GetFuture(inst);
            var q = PositionSizer.FromAtrStop(price, atr, StopAtrMult,
                future, equity, RiskPerTrade, MaxMarginRatio, MaxPosition);
            if (q <= 0) continue;

            _ctx.MarketBuy(inst, q, $"L_Z={z:F2}");
            var s = _state[inst];
            s.EntryPrice = price;
            s.StopLoss = price - StopAtrMult * atr;
            s.TakeProfit = TakeProfitAtrMult > 0 ? price + TakeProfitAtrMult * atr : 0;
            s.Direction = "Long";
            opened++;
        }

        // 做空 BottomN (Z-score 最低)
        for (int i = rankings.Count - 1; i >= Math.Max(0, rankings.Count - TopN); i--)
        {
            var (inst, z, atr, price) = rankings[i];
            if (atr / price < 0.001) continue;

            var future = _ctx.GetFuture(inst);
            var q = PositionSizer.FromAtrStop(price, atr, StopAtrMult,
                future, equity, RiskPerTrade, MaxMarginRatio, MaxPosition);
            if (q <= 0) continue;

            _ctx.MarketSell(inst, q, $"S_Z={z:F2}");
            var s = _state[inst];
            s.EntryPrice = price;
            s.StopLoss = price + StopAtrMult * atr;
            s.TakeProfit = TakeProfitAtrMult > 0 ? price - TakeProfitAtrMult * atr : 0;
            s.Direction = "Short";
            opened++;
        }

        if (opened > 0)
            _ctx.Log($"调仓: {rankings.Count}品种 → Long/Short x{Math.Min(TopN, rankings.Count)} → 开仓 {opened}");
    }

    // ═══════════════════════════════════════════════════════════════
    // InstrumentState (v2: 精简 — Z-score 由 IntradayMomFactor 管理)
    // ═══════════════════════════════════════════════════════════════

    private sealed class InstrumentState
    {
        private readonly AtrIndicator _atr;

        public double Atr, EntryPrice, StopLoss, TakeProfit, LastPrice;
        public string? Direction;

        public InstrumentState(int atrPeriod)
        {
            _atr = new AtrIndicator(atrPeriod);
        }

        public void UpdateAtr(Bar bar)
        {
            _atr.Update(bar);
            if (_atr.IsReady) Atr = _atr.CurrentValue;
            LastPrice = bar.CloseDouble;
        }

        public void ResetTrade()
        {
            EntryPrice = StopLoss = TakeProfit = 0;
            Direction = null;
        }
    }
}
