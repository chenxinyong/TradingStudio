using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;
using TradingStudio.Data.Factors;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// Carry 反转因子 — 横截面月度多空策略。
///
/// 信号: Carry = (近月−次月)/次月（负=深升水，正=深贴水）。
/// 方向: 做多低 Carry（深升水）TopN、做空高 Carry（深贴水）TopN —— 反转（Q1−Q5）。
/// 调仓: 日历月首次 Bar 触发，月度换手 12 次/年，成本敏感度低（相对日内因子的核心卖点）。
/// 收益: 用连续合约 xxx000（回测引擎天然喂的就是连续合约），与 Python 脚本一致。
/// 仓位: ATR 波动率缩放（逆波动加权），每腿风险 = RiskPerTrade，低波动品种自动多给手数。
///
/// Carry 面板由 CarryPanelBuilder 从 bars_day 单月合约现算（C# 内生成，不走 Python→CSV）。
/// 这是 Carry 与 FactorBase(单序列) 的架构差异：截面+日频+月度，需两张价格序列，故用面板预计算。
///
/// 验收: 独立回测扣成本后 Sharpe ≥ 1（复现基准 1.44 见 CarryEvalCommand）。
/// </summary>
public class CarryReversalStrategy : IStrategy
{
    // ── 策略参数 ──

    public StrategyParam<int> TopN { get; } = new("TopN", 8)
        { Group = "Portfolio", Description = "做多/做空品种数", OptimizeRange = (2, 20, 2) };

    public StrategyParam<string> CarryDbPath { get; } = new("CarryDbPath", @"C:\Works\Datas\bars_history.duckdb")
        { Group = "Data", Description = "bars_day 库路径（Initialize 时现算 carry 面板）" };

    public StrategyParam<int> AtrPeriod { get; } = new("AtrPeriod", 20)
        { Group = "Risk", Description = "ATR周期（仅用于仓位规模，非止损）" };

    public StrategyParam<double> StopAtrMult { get; } = new("StopAtrMult", 3.0)
        { Group = "Risk", Description = "ATR止损倍数（仓位规模分母，非实盘止损）" };

    public StrategyParam<double> RiskPerTrade { get; } = new("RiskPerTrade", 0.01)
        { Group = "Position", Description = "单品种单笔风险占比", OptimizeRange = (0.005, 0.03, 0.005) };

    public StrategyParam<double> MaxMarginRatio { get; } = new("MaxMarginRatio", 0.30)
        { Group = "Position", Description = "单品种保证金占比上限", OptimizeRange = (0.10, 0.60, 0.05) };

    public StrategyParam<int> MaxPosition { get; } = new("MaxPosition", 10)
        { Group = "Position", Description = "单品种最大手数", OptimizeRange = (1, 20, 1) };

    public string Name => "Carry反转(月度截面多空)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();
    private readonly Dictionary<string, string> _prodOf = new();   // inst(连续) → product 小写
    private Dictionary<DateTime, Dictionary<string, double>> _carry = new();

    private int _lastRebalanceMonth = -1;

    public void Initialize(StrategyContext context)
    {
        _ctx = context;

        // 1. 现算 carry 面板（bars_day 单月合约）
        _ctx.Log($"加载 carry 面板: {CarryDbPath.Value}");
        var panel = CarryPanelBuilder.BuildAsync(CarryDbPath.Value).GetAwaiter().GetResult();
        _carry = panel.Carry;
        _ctx.Log($"carry 面板就绪: {_carry.Count} 天 × {panel.Products.Count} 品种");

        // 2. 映射连续合约 → 产品码，初始化 ATR 状态
        foreach (var inst in context.SubscribedInstruments)
        {
            _prodOf[inst] = inst.ToLowerInvariant().Replace("000", "");
            _state[inst] = new InstrumentState(AtrPeriod.Value);
        }

        // 3. 预热 ATR（仅用于仓位规模）
        foreach (var inst in context.SubscribedInstruments)
        {
            var s = _state[inst];
            foreach (var bar in context.GetBarHistory(inst))
                s.UpdateAtr(bar);
        }
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;
        s.UpdateAtr(bar);

        if (_ctx.IsWarmup) return;

        // 日历月首次 Bar → 月度调仓
        int month = bar.TradingDay.Year * 12 + bar.TradingDay.Month;
        if (month == _lastRebalanceMonth) return;
        _lastRebalanceMonth = month;

        CloseAllPositions();
        RebalancePortfolio(bar.TradingDay.ToDateTime(TimeOnly.MinValue));
    }

    public void OnOrderEvent(OrderEvent evt) { }

    public void OnEndOfAlgorithm() { }

    private void CloseAllPositions()
    {
        foreach (var pos in _ctx.Positions)
            if (pos.Quantity != 0)
                _ctx.ClosePosition(pos.InstrumentId, "Rebalance");
    }

    private void RebalancePortfolio(DateTime day)
    {
        if (!_carry.TryGetValue(day, out var carryByProd))
        {
            _ctx.LogWarning($"调仓 {day:yyyy-MM-dd}: 该日无 carry 面板数据，跳过");
            return;
        }

        // 收集「有 carry 且 ATR 已就绪」的品种，按 carry 升序
        var rankings = new List<(string Inst, double Carry, double Atr, double Price)>();
        foreach (var (inst, prod) in _prodOf)
        {
            if (!carryByProd.TryGetValue(prod, out var carry)) continue;
            var s = _state[inst];
            if (s.Atr <= 0 || s.LastPrice <= 0) continue;
            rankings.Add((inst, carry, s.Atr, s.LastPrice));
        }

        if (rankings.Count < TopN.Value * 2)
        {
            _ctx.LogWarning($"调仓 {day:yyyy-MM-dd}: 有效品种 {rankings.Count} < {TopN.Value * 2}，跳过");
            return;
        }

        rankings.Sort((a, b) => a.Carry.CompareTo(b.Carry));   // 升序: 最低 carry = 深升水
        double equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);

        // 做多 TopN 最低 carry（深升水）
        for (int i = 0; i < Math.Min(TopN.Value, rankings.Count); i++)
        {
            var (inst, carry, atr, price) = rankings[i];
            var q = PositionSizer.FromAtrStop(price, atr, StopAtrMult.Value,
                _ctx.GetFuture(inst), equity, RiskPerTrade.Value, MaxMarginRatio.Value, MaxPosition.Value);
            if (q <= 0) continue;
            _ctx.MarketBuy(inst, q, $"carry={carry:F4}");
        }

        // 做空 TopN 最高 carry（深贴水）
        for (int i = rankings.Count - 1; i >= Math.Max(0, rankings.Count - TopN.Value); i--)
        {
            var (inst, carry, atr, price) = rankings[i];
            var q = PositionSizer.FromAtrStop(price, atr, StopAtrMult.Value,
                _ctx.GetFuture(inst), equity, RiskPerTrade.Value, MaxMarginRatio.Value, MaxPosition.Value);
            if (q <= 0) continue;
            _ctx.MarketSell(inst, q, $"carry={carry:F4}");
        }

        _ctx.Log($"调仓 {day:yyyy-MM-dd}: {rankings.Count} 品种 → Long/Short x{Math.Min(TopN.Value, rankings.Count)}");
    }

    private sealed class InstrumentState
    {
        private readonly AtrIndicator _atr;
        public double Atr, LastPrice;

        public InstrumentState(int atrPeriod) => _atr = new AtrIndicator(atrPeriod);

        public void UpdateAtr(Bar bar)
        {
            _atr.Update(bar);
            if (_atr.IsReady) Atr = _atr.CurrentValue;
            LastPrice = bar.CloseDouble;
        }
    }
}
