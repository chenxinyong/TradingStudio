using TradingStudio.Core.Engine;
using TradingStudio.Core.Factors;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// ML复合因子横截面策略 — 用 C# 原生因子实时计算 IC_Weighted 复合信号。
///
/// 替代 Python预计算→CSV→C#加载 的离线管线。
/// 因子: IntradayMom + VWAP_Dev (日内实时计算), Amihud + VolumeRatio (从日线历史预加载)
/// 权重: IC_Weighted (来自 Phase2 ML分析)
/// 交易: 每日横截面排名 → TopN Long + BottomN Short
/// </summary>
public class CompositeFactorStrategy : IStrategy
{
    public StrategyParam<int> TopN { get; } = new("TopN", 5)
        { Group = "Portfolio", Description = "做多/做空品种数", OptimizeRange = (1, 10, 1) };

    public StrategyParam<int> AtrPeriod { get; } = new("AtrPeriod", 20)
        { Group = "Risk", Description = "ATR周期", OptimizeRange = (10, 40, 10) };

    public StrategyParam<double> StopAtrMult { get; } = new("StopAtrMult", 3.0)
        { Group = "Risk", Description = "止损ATR倍数", OptimizeRange = (1.0, 10.0, 1.0) };

    public StrategyParam<double> RiskPerTrade { get; } = new("RiskPerTrade", 0.01)
        { Group = "Position", Description = "单品种风险占比", OptimizeRange = (0.005, 0.03, 0.005) };

    public StrategyParam<int> MaxPosition { get; } = new("MaxPosition", 2)
        { Group = "Position", Description = "最大持仓手数", OptimizeRange = (1, 10, 1) };

    // IC_Weighted 权重 (Phase2 ML OOS窗口平均)
    public StrategyParam<double> W_IntradayMom { get; } = new("W_IntradayMom", 0.22)
        { Group = "Weights", Description = "IntradayMom权重" };

    public StrategyParam<double> W_VwapDev { get; } = new("W_VwapDev", -0.27)
        { Group = "Weights", Description = "VWAP_Dev权重" };

    public StrategyParam<double> W_Amihud { get; } = new("W_Amihud", 0.33)
        { Group = "Weights", Description = "Amihud权重" };

    public StrategyParam<double> W_VolumeRatio { get; } = new("W_VolumeRatio", 0.17)
        { Group = "Weights", Description = "VolumeRatio权重" };

    public string Name => "ML复合因子(IC_Weighted)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();
    private DateOnly _lastTradingDay;
    private bool _firstDay = true;

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        foreach (var inst in context.SubscribedInstruments)
        {
            var s = new InstrumentState(AtrPeriod, 252);
            var history = context.GetBarHistory(inst);
            foreach (var bar in history)
            {
                s.UpdateAtr(bar);
                s.FeedBar(bar);
            }
            _state[inst] = s;
        }
        context.Log($"复合因子策略: {_state.Count}品种, TopN={TopN}, " +
            $"权重=[IM:{W_IntradayMom}, VD:{W_VwapDev}, AM:{W_Amihud}, VR:{W_VolumeRatio}]");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        if (_ctx.IsWarmup)
        {
            s.UpdateAtr(bar);
            s.FeedBar(bar);
            return;
        }

        // 日切检测
        if (bar.TradingDay != _lastTradingDay)
        {
            if (!_firstDay)
            {
                CloseAllPositions();
                RebalancePortfolio();
            }
            _firstDay = false;
            _lastTradingDay = bar.TradingDay;
        }

        s.UpdateAtr(bar);
        s.FeedBar(bar);

        // 止损检查
        var pos = _ctx.GetPosition(bar.InstrumentId);
        if (pos is not null && pos.Quantity != 0 && s.EntryPrice > 0)
        {
            bool exit = false;
            if (pos.Quantity > 0 && bar.LowDouble <= s.StopLoss) exit = true;
            else if (pos.Quantity < 0 && bar.HighDouble >= s.StopLoss) exit = true;
            if (exit)
            {
                _ctx.ClosePosition(bar.InstrumentId, "SL");
                s.ResetTrade();
            }
        }
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Rejected && _state.TryGetValue(evt.InstrumentId, out var s))
            s.ResetTrade();
    }

    public void OnEndOfAlgorithm() { }

    // ═══════════════════════════════════════════════════════════════

    private void RebalancePortfolio()
    {
        var rankings = new List<(string Inst, double Score, double Atr, double Price)>();
        foreach (var (inst, s) in _state)
        {
            if (!s.IsReady || s.Atr <= 0) continue;
            var score = W_IntradayMom * s.IntradayMomZ +
                        W_VwapDev * s.VwapDevZ +
                        W_Amihud * s.AmihudZ +
                        W_VolumeRatio * s.VolumeRatioZ;
            rankings.Add((inst, score, s.Atr, s.LastPrice));
        }

        if (rankings.Count < TopN * 2) return;

        // Ascending: lowest composite = LONG (VWAP_Dev dominates with negative weight)
        rankings.Sort((a, b) => a.Score.CompareTo(b.Score));

        var equity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        int opened = 0;

        // Long: Bottom N (lowest composite = highest predicted return)
        for (int i = 0; i < Math.Min(TopN, rankings.Count); i++)
        {
            var (inst, score, atr, price) = rankings[i];
            if (atr / price < 0.001) continue;
            var future = _ctx.GetFuture(inst);
            int q = PositionSizer.FromAtrStop(price, atr, StopAtrMult, future, equity, RiskPerTrade, 0.50, MaxPosition);
            if (q <= 0) continue;
            _ctx.MarketBuy(inst, q, $"L_S={score:F2}");
            var s = _state[inst];
            s.EntryPrice = price;
            s.StopLoss = price - StopAtrMult * atr;
            s.Direction = "Long";
            opened++;
        }

        // Short: Top N (highest composite = lowest predicted return)
        for (int i = rankings.Count - 1; i >= Math.Max(0, rankings.Count - TopN); i--)
        {
            var (inst, score, atr, price) = rankings[i];
            if (atr / price < 0.001) continue;
            var future = _ctx.GetFuture(inst);
            int q = PositionSizer.FromAtrStop(price, atr, StopAtrMult, future, equity, RiskPerTrade, 0.50, MaxPosition);
            if (q <= 0) continue;
            _ctx.MarketSell(inst, q, $"S_S={score:F2}");
            var s = _state[inst];
            s.EntryPrice = price;
            s.StopLoss = price + StopAtrMult * atr;
            s.Direction = "Short";
            opened++;
        }

        if (opened > 0)
            _ctx.Log($"调仓: {rankings.Count}品种 → 开仓{opened}个");
    }

    private void CloseAllPositions()
    {
        foreach (var pos in _ctx.Positions)
            if (pos.Quantity != 0)
                _ctx.ClosePosition(pos.InstrumentId, "EOD");
    }

    // ═══════════════════════════════════════════════════════════════

    private sealed class InstrumentState
    {
        private readonly IntradayMomFactor _im;
        private readonly VwapDevFactor _vd;
        private readonly AtrIndicator _atr;
        private readonly Queue<double> _amihudWindow, _volWindow;
        private double _volSum;
        private int _volCount;

        public double Atr, EntryPrice, StopLoss, LastPrice;
        public string? Direction;
        public double IntradayMomZ, VwapDevZ, AmihudZ, VolumeRatioZ;
        public bool IsReady => _im.IsReady;
        private DateOnly _currentDay;

        public InstrumentState(int atrPeriod, int windowSize)
        {
            _im = new IntradayMomFactor(windowSize);
            _vd = new VwapDevFactor(windowSize);
            _atr = new AtrIndicator(atrPeriod);
            _amihudWindow = new(windowSize);
            _volWindow = new(windowSize);
        }

        public void UpdateAtr(Bar bar) { _atr.Update(bar); if (_atr.IsReady) Atr = _atr.CurrentValue; }

        public void FeedBar(Bar bar)
        {
            LastPrice = bar.CloseDouble;
            bool isNewDay = bar.TradingDay != _currentDay;
            _currentDay = bar.TradingDay;

            // IntradayMom: use high as isNewDay signal, low as bar period (hack via IFactor.Update)
            var imVal = _im.Update(bar.CloseDouble, isNewDay ? 1 : 0, 15, 0);
            IntradayMomZ = imVal.IsValid ? imVal.ZScore : 0;

            // VWAP_Dev: use high as isNewDay signal
            var vdVal = _vd.Update(bar.CloseDouble, isNewDay ? 1 : 0, 0, bar.Volume);
            VwapDevZ = vdVal.IsValid ? vdVal.ZScore : 0;

            // VolumeRatio: rolling z-score of volume
            _volWindow.Enqueue(bar.Volume); _volSum += bar.Volume; _volCount++;
            if (_volWindow.Count > 252) { _volSum -= _volWindow.Dequeue(); _volCount--; }
            if (_volCount >= 60)
            {
                double volMean = _volSum / _volCount;
                double volStd = Math.Sqrt(_volWindow.Average(x => (x - volMean) * (x - volMean)));
                VolumeRatioZ = volStd > 1e-10 ? (bar.Volume - volMean) / volStd : 0;
            }
            else VolumeRatioZ = 0;

            // Amihud: simplified — use ATR/Close as liquidity proxy
            AmihudZ = Atr > 0 ? -(Atr / bar.CloseDouble) : 0;  // negative: lower liquidity = lower score
        }

        public void ResetTrade() { EntryPrice = StopLoss = 0; Direction = null; }
    }
}
