using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;
using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.Strategy;

/// <summary>
/// 缠论买卖点策略 — 用 SignalGenerator 的一二三类买卖点（背驰 → B1/B2/B3/S1/S2/S3）驱动交易。
/// 区别于 ChanLunStrategy（日线方向+30min笔顺势）：本策略是真正的缠论抄底/逃顶逻辑。
///
/// 无前视保证：信号以 ConfirmEnd（确认笔完成时间）为最早可交易时间戳，
/// 在 OnBar 里只处理 ConfirmEnd 已过的信号，入场用当前 bar 收盘价（而非分型理想价）。
/// 出场：反向买卖点信号反手 + ATR 追踪止损。
/// </summary>
public class ChanLunSignalStrategy : IStrategy
{
    public StrategyParam<int> MinBiLen { get; } = new("MinBiLen", 5)
        { Group = "Signal", Description = "最小笔长度(K线数)", OptimizeRange = (3, 10, 1) };

    public StrategyParam<int> AtrPeriod { get; } = new("AtrPeriod", 20)
        { Group = "Risk", Description = "ATR周期", OptimizeRange = (5, 60, 5) };

    public StrategyParam<double> StopAtrMult { get; } = new("StopAtrMult", 2.0)
        { Group = "Risk", Description = "止损ATR倍数", OptimizeRange = (1, 5, 0.5) };

    public StrategyParam<double> RiskPerTrade { get; } = new("RiskPerTrade", 0.02)
        { Group = "Position", Description = "单笔风险占比", OptimizeRange = (0.005, 0.1, 0.005) };

    public StrategyParam<double> MaxMarginRatio { get; } = new("MaxMarginRatio", 0.6)
        { Group = "Position", Description = "最大保证金占用比", OptimizeRange = (0.1, 0.9, 0.05) };

    public StrategyParam<bool> AllowShort { get; } = new("AllowShort", true)
        { Group = "Signal", Description = "允许做空(S1/S2/S3)" };

    public StrategyParam<bool> UseB2B3 { get; } = new("UseB2B3", true)
        { Group = "Signal", Description = "启用二/三类买卖点(仅一类=更稀缺更可靠)" };

    public string Name => "缠论买卖点(背驰+一二三类买卖点)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        foreach (var inst in context.SubscribedInstruments)
        {
            var history = context.GetBarHistory(inst);
            if (history.Count == 0)
            {
                context.LogWarning($"{inst}: 无历史数据，跳过");
                continue;
            }

            var s = new InstrumentState(inst, AtrPeriod);
            s.AllBars = history.ToList();
            foreach (var bar in history) s.UpdateAtr(bar);

            // 信号去重起点 = 预热期最后 bar 时间 - 1秒（边界：ConfirmEnd == 末 bar 时间的信号能在首根回测 bar 捕获）
            s.LastSignalTime = history[^1].BarTime.AddSeconds(-1);

            _state[inst] = s;
            context.Log($"{inst}: {history.Count} bars, ATR={s.CurrentAtr:F2}");
        }
        _ctx.Log($"初始化: {Name} [{string.Join(", ", context.SubscribedInstruments)}]");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;
        if (_ctx.IsWarmup) { s.UpdateAtr(bar); return; }

        s.UpdateAtr(bar);
        s.AllBars.Add(bar);

        // 全量重分析 → 生成一二三类买卖点信号
        var signals = GenerateSignals(s.AllBars);

        // 去重 + 按 ConfirmEnd 顺序处理新信号
        foreach (var sig in signals.OrderBy(x => x.ConfirmEnd))
        {
            if (sig.ConfirmEnd <= s.LastSignalTime) continue;
            s.LastSignalTime = sig.ConfirmEnd;
            ProcessSignal(s, bar, sig);
        }

        CheckStopLoss(s, bar);
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Filled)
            _ctx.Log($"成交: {evt.InstrumentId} {evt.Direction} {evt.Quantity}手 @ {evt.FillPrice:F2}");
        if (evt.Type is OrderEventType.Rejected or OrderEventType.Cancelled)
            _ctx.LogWarning($"{(evt.Type == OrderEventType.Rejected ? "拒单" : "撤单")}: {evt.InstrumentId} {evt.Message}");
    }

    public void OnEndOfAlgorithm()
    {
        foreach (var (inst, s) in _state)
        {
            _ctx.Log($"{inst}: Equity={_ctx.Equity:C}, Position={_ctx.GetPosition(inst)?.Quantity ?? 0}");
        }
    }

    // ═══ 信号生成 ═══

    private List<ChanSignal> GenerateSignals(List<Bar> coreBars)
    {
        var clBars = BarAdapter.FromCoreBars(coreBars);
        var result = ChanLunAnalyzer.Analyze(clBars, minBiLen: MinBiLen);
        var bis = result.Bis;
        var zs = result.Zhongshus;
        var std = result.StdBars;

        var signals = new List<ChanSignal>();
        var divs = SignalGenerator.CheckDivergence(bis, zs, std);

        foreach (var div in divs)
        {
            if (div.Type == "BOTTOM_DIVERGENCE")
            {
                var b1 = SignalGenerator.SignalB1(div, bis, zs);
                if (b1 != null) signals.Add(b1);
                if (UseB2B3)
                {
                    var b2 = SignalGenerator.SignalB2(bis, zs, div);
                    if (b2 != null) signals.Add(b2);
                }
            }
            else if (AllowShort) // TOP_DIVERGENCE
            {
                var s1 = SignalGenerator.SignalS1(div, bis, zs);
                if (s1 != null) signals.Add(s1);
                if (UseB2B3)
                {
                    var s2 = SignalGenerator.SignalS2(bis, zs, div);
                    if (s2 != null) signals.Add(s2);
                }
            }
        }

        if (UseB2B3)
        {
            signals.AddRange(SignalGenerator.SignalB3(bis, zs));
            if (AllowShort)
                signals.AddRange(SignalGenerator.SignalS3(bis, zs));
        }
        return signals;
    }

    // ═══ 信号执行 ═══

    private void ProcessSignal(InstrumentState s, Bar bar, ChanSignal sig)
    {
        bool isBuy = sig.Type is ChanSignalType.B1 or ChanSignalType.B2 or ChanSignalType.B3;
        var pos = _ctx.GetPosition(s.InstId);
        bool hasPos = pos is not null && pos.Quantity != 0;

        if (isBuy)
        {
            if (hasPos && pos!.Quantity < 0)   // 空单 → 反手平空
                _ctx.ClosePosition(s.InstId, $"反手:{sig.Description}");

            var after = _ctx.GetPosition(s.InstId);
            if (after is null || after.Quantity == 0)
                OpenPosition(s, bar, isLong: true, sig.Description);
        }
        else
        {
            if (hasPos && pos!.Quantity > 0)   // 多单 → 反手平多
                _ctx.ClosePosition(s.InstId, $"反手:{sig.Description}");

            var after = _ctx.GetPosition(s.InstId);
            if (after is null || after.Quantity == 0)
                OpenPosition(s, bar, isLong: false, sig.Description);
        }
    }

    private void OpenPosition(InstrumentState s, Bar bar, bool isLong, string desc)
    {
        if (s.CurrentAtr <= 0) return;
        var qty = PositionSizer.FromAtrStop(bar.CloseDouble, s.CurrentAtr, StopAtrMult,
            _ctx.GetFuture(s.InstId), (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
            RiskPerTrade, (double)MaxMarginRatio);
        if (qty <= 0) return;

        if (isLong)
        {
            _ctx.MarketBuy(s.InstId, qty, $"缠论:{desc}");
            s.StopPrice = bar.CloseDouble - StopAtrMult * s.CurrentAtr;
        }
        else
        {
            _ctx.MarketSell(s.InstId, qty, $"缠论:{desc}");
            s.StopPrice = bar.CloseDouble + StopAtrMult * s.CurrentAtr;
        }
        s.EntryPrice = bar.CloseDouble;
        _ctx.Log($"入场: {s.InstId} {(isLong ? "多" : "空")} x{qty} @ {bar.CloseDouble:F2} [{desc}]");
    }

    private void CheckStopLoss(InstrumentState s, Bar bar)
    {
        var pos = _ctx.GetPosition(s.InstId);
        if (pos is null || pos.Quantity == 0 || s.StopPrice <= 0) return;

        bool stopped = (pos.Quantity > 0 && bar.LowDouble <= s.StopPrice)
                    || (pos.Quantity < 0 && bar.HighDouble >= s.StopPrice);
        if (!stopped) return;

        var stopAt = s.StopPrice;
        _ctx.ClosePosition(s.InstId, "ATR_SL");
        s.StopPrice = 0; s.EntryPrice = 0;
        _ctx.Log($"止损: {s.InstId} @ {stopAt:F2}");
    }

    // ═══ State ═══

    private class InstrumentState
    {
        public string InstId;
        public int AtrPeriod;
        public List<Bar> AllBars = [];
        public DateTime LastSignalTime;
        public double CurrentAtr { get; private set; }
        public double StopPrice;
        public double EntryPrice;

        private readonly Queue<double> _trWindow;
        private double _trSum;
        private double _prevClose = double.NaN;

        public InstrumentState(string inst, int atrPeriod)
        {
            InstId = inst;
            AtrPeriod = atrPeriod;
            _trWindow = new Queue<double>(atrPeriod + 1);
        }

        public void UpdateAtr(Bar bar)
        {
            if (!double.IsNaN(_prevClose))
            {
                var tr = Math.Max(bar.HighDouble - bar.LowDouble,
                    Math.Max(Math.Abs(bar.HighDouble - _prevClose), Math.Abs(bar.LowDouble - _prevClose)));
                _trWindow.Enqueue(tr); _trSum += tr;
                if (_trWindow.Count > AtrPeriod) _trSum -= _trWindow.Dequeue();
                if (_trWindow.Count >= AtrPeriod) CurrentAtr = _trSum / AtrPeriod;
            }
            _prevClose = bar.CloseDouble;
        }
    }
}
