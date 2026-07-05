using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Sizing;
using TradingStudio.Core.Strategy;
using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.Strategy;

/// <summary>
/// MTF趋势 + 缠论入场 融合策略。
/// 日线 SMA(233) 定趋势方向（只做顺势），30min ChanLun笔完成 定入场时机。
/// 出场: 反向笔完成 或 ATR追踪止损。
/// </summary>
public class MtfChanLunStrategy : IStrategy
{
    [StrategyParameter(Description = "日线趋势SMA周期", DefaultValue = 233, Min = 50, Max = 400, Category = "Trend")]
    public int TrendPeriod { get; set; } = 233;

    [StrategyParameter(Description = "ATR周期", DefaultValue = 20, Min = 5, Max = 60, Category = "Risk")]
    public int AtrPeriod { get; set; } = 20;

    [StrategyParameter(Description = "止损ATR倍数", DefaultValue = 2.5, Min = 1.0, Max = 5.0, Category = "Risk")]
    public double StopAtrMult { get; set; } = 2.5;

    [StrategyParameter(Description = "最小笔力度(万分比)", DefaultValue = 50, Min = 10, Max = 500, Category = "Signal")]
    public double MinBiPower { get; set; } = 50;

    [StrategyParameter(Description = "最小笔长度(K线数)", DefaultValue = 2, Min = 1, Max = 10, Category = "Signal")]
    public int MinBiLen { get; set; } = 2;

    [StrategyParameter(Description = "单笔风险占比", DefaultValue = 0.02, Min = 0.005, Max = 0.1, Category = "Position")]
    public double RiskPerTrade { get; set; } = 0.02;

    [StrategyParameter(Description = "最大保证金占用比", DefaultValue = 0.6, Min = 0.1, Max = 0.9, Category = "Position")]
    public double MaxMarginRatio { get; set; } = 0.6;

    [StrategyParameter(Description = "要求价格在SMA同侧(1=同侧,-1=反向,0=不要求)", DefaultValue = 0, Min = -1, Max = 1, Category = "Entry")]
    public int RequirePriceSide { get; set; } = 0;

    public string Name => "MTF缠论融合(日线趋势+30min笔)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, State> _state = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        foreach (var inst in context.SubscribedInstruments)
        {
            var future = context.GetFuture(inst);
            var multiplier = (double)(future?.TradingUnit ?? 10m);
            var marginRate = (double)(future?.MarginRate ?? 0.08m);
            var history = context.GetBarHistory(inst);
            if (history.Count == 0) { context.LogWarning($"{inst}: 无历史数据"); continue; }

            var s = new State(inst, multiplier, marginRate, AtrPeriod);
            s.AllBars = history.ToList();

            // 日线 SMA 趋势
            var dayBars = BuildDayBars(history);
            s.DayBars = dayBars;
            s.DaySma = ComputeSma(dayBars.Select(b => b.CloseDouble).ToList(), TrendPeriod);
            if (dayBars.Count > 0)
            {
                s.CurrentDayBar = dayBars[^1]; s.HasCurrentDayBar = true;
                s.DayBars.RemoveAt(s.DayBars.Count - 1);
                s.LastDayUpdate = DateOnly.FromDateTime(s.CurrentDayBar.BarTime);
            }

            // 30min 缠论笔
            var clBars = BarAdapter.FromCoreBars(s.AllBars);
            var clResult = ChanLunAnalyzer.Analyze(clBars, minBiLen: MinBiLen);
            s.AllBis = clResult.Bis;
            s.BiEvents = clResult.Bis.Select(bi => (bi.DtEnd, bi)).OrderBy(x => x.DtEnd).ToList();

            // ATR
            foreach (var bar in history) s.UpdateAtr(bar);

            // 预处理：跳过预热期 Bi
            s.LastBarTime = history[^1].BarTime.AddSeconds(-1);
            while (s.NextBiIdx < s.BiEvents.Count)
            {
                if (s.BiEvents[s.NextBiIdx].DtEnd > s.LastBarTime) break;
                s.NextBiIdx++;
            }

            _state[inst] = s;
            var trend = s.DaySma.Count >= 2 && s.DaySma[^1] > s.DaySma[^2] ? "UP" :
                         s.DaySma.Count >= 2 ? "DOWN" : "N/A";
            context.Log($"{inst}: Trend={trend} SMA={s.DaySma.Count}d Bi={clResult.BiCount} ATR={s.CurrentAtr:F2}");
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

        // 日线 SMA 增量
        var barDay = DateOnly.FromDateTime(bar.BarTime);
        bool isNewDay = !s.HasCurrentDayBar || barDay != s.LastDayUpdate;
        if (isNewDay)
        {
            if (s.HasCurrentDayBar) { s.DayBars.Add(s.CurrentDayBar); UpdateDaySma(s); }
            s.CurrentDayBar = new Bar { InstrumentId = bar.InstrumentId, TradingDay = bar.TradingDay,
                BarTime = barDay.ToDateTime(TimeOnly.MinValue), Open = bar.Open, High = bar.High,
                Low = bar.Low, Close = bar.Close, Volume = bar.Volume };
            s.HasCurrentDayBar = true; s.LastDayUpdate = barDay;
        }
        else
        {
            if (bar.High > s.CurrentDayBar.High) s.CurrentDayBar.High = bar.High;
            if (bar.Low < s.CurrentDayBar.Low) s.CurrentDayBar.Low = bar.Low;
            s.CurrentDayBar.Close = bar.Close; s.CurrentDayBar.Volume += bar.Volume;
        }

        // 趋势方向
        if (s.DaySma.Count < 2) return;
        var trendUp = s.DaySma[^1] > s.DaySma[^2];

        // 30min 笔增量检测
        var clBars = BarAdapter.FromCoreBars(s.AllBars);
        var clResult = ChanLunAnalyzer.Analyze(clBars, minBiLen: MinBiLen);
        if (clResult.Bis.Count > s.AllBis.Count)
        {
            for (int i = s.AllBis.Count; i < clResult.Bis.Count; i++)
                if (clResult.Bis[i].DtEnd <= s.AllBars[^1].BarTime)
                    s.BiEvents.Add((clResult.Bis[i].DtEnd, clResult.Bis[i]));
            s.AllBis = clResult.Bis;
            s.BiEvents = s.BiEvents.OrderBy(x => x.DtEnd).ToList();
        }

        // 处理 Bi 完成事件
        ProcessBiEvents(s, bar, trendUp);
        CheckStop(s, bar);
        s.LastBarTime = bar.BarTime.AddSeconds(-1);
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Filled)
            _ctx.Log($"成交: {evt.InstrumentId} {evt.Direction} {evt.Quantity}手 @ {evt.FillPrice:F2}");
    }
    public void OnEndOfAlgorithm() { }

    // ═══ Bi 事件处理 ═══

    private void ProcessBiEvents(State s, Bar bar, bool trendUp)
    {
        while (s.NextBiIdx < s.BiEvents.Count)
        {
            var (endTime, bi) = s.BiEvents[s.NextBiIdx];
            if (endTime > bar.BarTime) break;
            if (endTime > s.LastBarTime || s.LastBarTime == default)
                ProcessBi(s, bar, bi, trendUp);
            s.NextBiIdx++;
        }
    }

    private void ProcessBi(State s, Bar bar, Bi bi, bool trendUp)
    {
        if (bi.Power < MinBiPower || bi.BarCount < MinBiLen) return;
        if (s.CurrentAtr <= 0) return;

        var pos = _ctx.GetPosition(s.InstId);
        bool hasPos = pos is not null && pos.Quantity != 0;

        // 出场：反向笔
        if (hasPos)
        {
            bool exit = (pos!.Quantity > 0 && bi.Type == Direction.Down)
                     || (pos.Quantity < 0 && bi.Type == Direction.Up);
            if (exit) { _ctx.ClosePosition(s.InstId); s.Trail = 0; }
            return;
        }

        // 入场：笔方向 = 日线趋势方向 + 可选价格侧确认
        if (trendUp && bi.Type == Direction.Up)
        {
            if (RequirePriceSide == 1 && bar.CloseDouble < s.DaySma[^1]) return;   // 要求价格在SMA上方
            if (RequirePriceSide == -1 && bar.CloseDouble > s.DaySma[^1]) return;  // 要求价格在SMA下方(抄底)
            var q = PositionSizer.FromAtrStop(bar.CloseDouble, s.CurrentAtr, StopAtrMult,
                    _ctx.GetFuture(s.InstId), (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
                    RiskPerTrade, (double)MaxMarginRatio);
            if (q > 0) { _ctx.MarketBuy(s.InstId, q, "MTF+CL做多"); s.Trail = bar.CloseDouble - StopAtrMult * s.CurrentAtr; }
        }
        else if (!trendUp && bi.Type == Direction.Down)
        {
            if (RequirePriceSide == 1 && bar.CloseDouble > s.DaySma[^1]) return;
            if (RequirePriceSide == -1 && bar.CloseDouble < s.DaySma[^1]) return;
            var q = PositionSizer.FromAtrStop(bar.CloseDouble, s.CurrentAtr, StopAtrMult,
                    _ctx.GetFuture(s.InstId), (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital),
                    RiskPerTrade, (double)MaxMarginRatio);
            if (q > 0) { _ctx.MarketSell(s.InstId, q, "MTF+CL做空"); s.Trail = bar.CloseDouble + StopAtrMult * s.CurrentAtr; }
        }
    }

    private void CheckStop(State s, Bar bar)
    {
        var pos = _ctx.GetPosition(s.InstId);
        if (pos is null || pos.Quantity == 0 || s.Trail <= 0) return;
        bool stopped = (pos.Quantity > 0 && bar.LowDouble <= s.Trail)
                    || (pos.Quantity < 0 && bar.HighDouble >= s.Trail);
        if (stopped) { _ctx.ClosePosition(s.InstId); s.Trail = 0; }
    }

    // ═══ 日线工具 ═══

    private void UpdateDaySma(State s)
    {
        var prices = s.DayBars.Select(b => b.CloseDouble).ToList();
        s.DaySma = ComputeSma(prices, TrendPeriod);
    }

    private static List<double> ComputeSma(List<double> prices, int period)
    {
        var result = new List<double>(prices.Count);
        double sum = 0; var q = new Queue<double>();
        foreach (var p in prices)
        {
            q.Enqueue(p); sum += p;
            if (q.Count > period) sum -= q.Dequeue();
            result.Add(q.Count >= period ? sum / period : double.NaN);
        }
        return result;
    }

    private static List<Bar> BuildDayBars(IReadOnlyList<Bar> bars)
    {
        var result = new List<Bar>();
        DateOnly? curDay = null; Bar cur = default;
        foreach (var b in bars)
        {
            var d = DateOnly.FromDateTime(b.BarTime);
            if (d != curDay) { if (curDay != null) result.Add(cur); curDay = d;
                cur = new Bar { InstrumentId = b.InstrumentId, TradingDay = b.TradingDay,
                    BarTime = d.ToDateTime(TimeOnly.MinValue), Open = b.Open, High = b.High,
                    Low = b.Low, Close = b.Close, Volume = b.Volume };
            } else { if (b.High > cur.High) cur.High = b.High; if (b.Low < cur.Low) cur.Low = b.Low;
                cur.Close = b.Close; cur.Volume += b.Volume; }
        }
        if (curDay != null) result.Add(cur);
        return result;
    }

    // ═══ State ═══

    private class State
    {
        public string InstId;
        public double ContractMultiplier, MarginRate;
        public int AtrPeriod;
        public List<Bar> AllBars = [];
        public List<Bar> DayBars = [];
        public Bar CurrentDayBar; public bool HasCurrentDayBar;
        public DateOnly LastDayUpdate;
        public List<double> DaySma = [];
        public List<Bi> AllBis = [];
        public List<(DateTime DtEnd, Bi Bi)> BiEvents = [];
        public int NextBiIdx;
        public double CurrentAtr, Trail;
        public DateTime LastBarTime;

        public State(string id, double mult, double margin, int atr)
        { InstId = id; ContractMultiplier = mult; MarginRate = margin; AtrPeriod = atr; }

        // ATR内部队列
        private Queue<double> _trQ = new(); private double _trSum, _prevC = double.NaN;
        public void UpdateAtr(Bar bar)
        {
            if (!double.IsNaN(_prevC))
            {
                var tr = Math.Max(bar.HighDouble - bar.LowDouble,
                    Math.Max(Math.Abs(bar.HighDouble - _prevC), Math.Abs(bar.LowDouble - _prevC)));
                _trQ.Enqueue(tr); _trSum += tr;
                if (_trQ.Count > AtrPeriod) _trSum -= _trQ.Dequeue();
                if (_trQ.Count >= AtrPeriod) CurrentAtr = _trSum / AtrPeriod;
            }
            _prevC = bar.CloseDouble;
        }
    }
}
