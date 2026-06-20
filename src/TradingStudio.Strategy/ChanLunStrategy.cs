using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.Strategy;

public class ChanLunStrategy : IStrategy
{
    [StrategyParameter(Description = "ATR周期", DefaultValue = 20, Min = 5, Max = 60, Category = "Risk")]
    public int AtrPeriod { get; set; } = 20;

    [StrategyParameter(Description = "止损ATR倍数", DefaultValue = 2, Min = 1, Max = 5, Category = "Risk")]
    public double StopAtrMult { get; set; } = 2.0;

    [StrategyParameter(Description = "最小笔力度(万分比)", DefaultValue = 200, Min = 10, Max = 500, Category = "Signal")]
    public double MinBiPower { get; set; } = 200;

    [StrategyParameter(Description = "最小笔长度(K线数)", DefaultValue = 3, Min = 2, Max = 10, Category = "Signal")]
    public int MinBiLen { get; set; } = 3;

    [StrategyParameter(Description = "单笔风险占比", DefaultValue = 0.02, Min = 0.005, Max = 0.1, Category = "Position")]
    public double RiskPerTrade { get; set; } = 0.02;

    [StrategyParameter(Description = "最大保证金占用比", DefaultValue = 0.6, Min = 0.1, Max = 0.9, Category = "Position")]
    public double MaxMarginRatio { get; set; } = 0.6;

    public string Name => "缠论多级别策略(日线+30min)";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;

        foreach (var inst in context.SubscribedInstruments)
        {
            var future = context.GetFuture(inst);
            var multiplier = future?.TradingUnit ?? 20m;
            var marginRate = future?.MarginRate ?? 0.08m;

            var history = context.GetBarHistory(inst);
            if (history.Count == 0)
            {
                context.LogWarning($"{inst}: 无历史数据，跳过");
                continue;
            }

            var state = new InstrumentState(inst, (double)multiplier, (double)marginRate,
                AtrPeriod, StopAtrMult, MinBiPower, MinBiLen);
            _state[inst] = state;

            // 保存全部 Bar 用于增量 Bi 检测
            state.AllBars = history.ToList();

            var dayCoreBars = BuildDayBars(history);
            var dayChanlunBars = BarAdapter.FromCoreBars(dayCoreBars);
            var dayResult = ChanLunAnalyzer.Analyze(dayChanlunBars, minBiLen: 5);
            state.DayBis = dayResult.Bis;

            var m30Bars = BarAdapter.FromCoreBars(state.AllBars);
            var m30Result = ChanLunAnalyzer.Analyze(m30Bars, minBiLen: MinBiLen);
            state.All30mBis = m30Result.Bis;

            state.BiEndEvents = m30Result.Bis
                .Select(bi => (bi.DtEnd, bi))
                .OrderBy(x => x.DtEnd)
                .ToList();

            foreach (var bar in history)
                state.UpdateAtr(bar);

            // 预处理：跳过所有预热期 Bi 事件（EndTime ≤ 最后预热 Bar 时间），避免涌入实盘首根 Bar
            // 注：LastBarTime - 1 秒，确保 EndTime == LastBarTime 的 Bi 能被 > 条件捕获
            state.LastBarTime = history.Count > 0 ? history[^1].BarTime.AddSeconds(-1) : DateTime.MinValue;
            while (state.NextBiEventIdx < state.BiEndEvents.Count)
            {
                var (endTime, _) = state.BiEndEvents[state.NextBiEventIdx];
                if (endTime > state.LastBarTime) break;
                state.NextBiEventIdx++;
            }

            context.Log($"{inst}: Day={dayResult.BiCount}BI, 30min={m30Result.BiCount}BI, Pending={state.BiEndEvents.Count - state.NextBiEventIdx}Bi, ATR={state.CurrentAtr:F2}");
        }

        _ctx.Log($"初始化: {Name} on [{string.Join(", ", context.SubscribedInstruments)}]");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        if (_ctx.IsWarmup)
        {
            s.UpdateAtr(bar);
            // AllBars 已在 Initialize 中加载，LastBarTime 也由 Initialize 设好
            return;
        }

        s.UpdateAtr(bar);
        s.AllBars.Add(bar);
        if (s.HasPendingEntry) s.HasPendingEntry = false;

        var dayDir = GetCurrentDayDirection(s, bar.BarTime);
        if (dayDir == DirectionType.Unknown) return;

        // 增量检测新完成的 Bi
        DetectNewBis(s);

        CheckBiCompletions(s, bar, dayDir);
        CheckStopLoss(s, bar);
        s.LastBarTime = bar.BarTime.AddSeconds(-1);  // 边界：确保 EndTime == bar.BarTime 的 Bi 在下根 Bar 能被 > 捕获
    }

    /// <summary>重新分析 30min Bar 序列，找出新完成的 Bi</summary>
    private void DetectNewBis(InstrumentState s)
    {
        if (s.AllBars.Count == 0) return;
        var chanlunBars = BarAdapter.FromCoreBars(s.AllBars);
        var result = ChanLunAnalyzer.Analyze(chanlunBars, minBiLen: MinBiLen);

        // 比较新旧 Bi 列表，找出新增的完成 Bi
        var prevCount = s.All30mBis.Count;
        if (result.Bis.Count <= prevCount) return;

        // 把新增的 Bi 追加到事件队列
        for (int i = prevCount; i < result.Bis.Count; i++)
        {
            var bi = result.Bis[i];
            // 只加入已完成的 Bi（Bi.EndTime <= 最后一根 Bar 的时间）
            if (bi.DtEnd <= s.AllBars[^1].BarTime)
                s.BiEndEvents.Add((bi.DtEnd, bi));
        }
        s.All30mBis = result.Bis;

        // 重新排序事件队列（首次不重置索引——Initialize中已设置好）
        s.BiEndEvents = s.BiEndEvents.OrderBy(x => x.EndTime).ToList();
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Filled)
            _ctx.Log($"成交: {evt.InstrumentId} {evt.Direction} {evt.Quantity}手 @ {evt.FillPrice:F2} [{evt.Message}]");
        if (evt.Type == OrderEventType.Rejected)
        {
            _ctx.LogWarning($"拒单: {evt.InstrumentId} {evt.Message}");
            if (_state.TryGetValue(evt.InstrumentId, out var s)) s.HasPendingEntry = false;
        }
    }

    public void OnEndOfAlgorithm()
    {
        foreach (var (inst, s) in _state)
            _ctx.Log($"{inst}: Equity={_ctx.Equity:C}, Position={_ctx.GetPosition(inst)?.Quantity ?? 0}");
    }

    private enum DirectionType { Up, Down, Unknown }

    private DirectionType GetCurrentDayDirection(InstrumentState s, DateTime barTime)
    {
        foreach (var bi in s.DayBis)
            if (bi.DtStart <= barTime && barTime <= bi.DtEnd)
                return bi.Type == ChanLun.Direction.Up ? DirectionType.Up : DirectionType.Down;
        if (s.DayBis.Count > 0 && barTime > s.DayBis[^1].DtEnd)
            return s.DayBis[^1].Type == ChanLun.Direction.Up ? DirectionType.Up : DirectionType.Down;
        return DirectionType.Unknown;
    }

    private void CheckBiCompletions(InstrumentState s, Bar bar, DirectionType dayDir)
    {
        while (s.NextBiEventIdx < s.BiEndEvents.Count)
        {
            var (endTime, bi) = s.BiEndEvents[s.NextBiEventIdx];
            if (endTime > bar.BarTime) break;
            if (endTime > s.LastBarTime || s.LastBarTime == default)
            {
                ProcessBiCompletion(s, bar, bi, dayDir);
            }
            s.NextBiEventIdx++;
        }
    }

    private void ProcessBiCompletion(InstrumentState s, Bar bar, Bi completedBi, DirectionType dayDir)
    {
        if (completedBi.Power < MinBiPower || completedBi.BarCount < MinBiLen)
            return;
        if (s.CurrentAtr <= 0) return;

        var pos = _ctx.GetPosition(s.InstrumentId);
        var hasPosition = pos is not null && pos.Quantity != 0;

        if (hasPosition)
        {
            bool shouldExit = false;
            if (pos!.Quantity > 0 && completedBi.Type == ChanLun.Direction.Down) shouldExit = true;
            else if (pos.Quantity < 0 && completedBi.Type == ChanLun.Direction.Up) shouldExit = true;

            if (shouldExit)
            {
                _ctx.ClosePosition(s.InstrumentId);
                s.HasPendingEntry = false; s.EntryBarTime = null; s.StopPrice = 0; s.ActiveBi = null;
                return;
            }
        }

        if (!hasPosition && !s.HasPendingEntry)
        {
            if (dayDir == DirectionType.Up && completedBi.Type == ChanLun.Direction.Up)
            {
                var qty = CalculatePosition(bar.CloseDouble, s, DirectionType.Up);
                if (qty > 0)
                {
                    _ctx.MarketBuy(s.InstrumentId, qty, $"BI入场: 日线Up+30min底分型");
                    s.HasPendingEntry = true; s.EntryBarTime = bar.BarTime;
                    s.StopPrice = bar.CloseDouble - StopAtrMult * s.CurrentAtr;
                    s.ActiveBi = completedBi;
                }
            }
            else if (dayDir == DirectionType.Down && completedBi.Type == ChanLun.Direction.Down)
            {
                var qty = CalculatePosition(bar.CloseDouble, s, DirectionType.Down);
                if (qty > 0)
                {
                    _ctx.MarketSell(s.InstrumentId, qty, $"BI入场: 日线Down+30min顶分型");
                    s.HasPendingEntry = true; s.EntryBarTime = bar.BarTime;
                    s.StopPrice = bar.CloseDouble + StopAtrMult * s.CurrentAtr;
                    s.ActiveBi = completedBi;
                }
            }
        }
    }

    private void CheckStopLoss(InstrumentState s, Bar bar)
    {
        var pos = _ctx.GetPosition(s.InstrumentId);
        if (pos is null || pos.Quantity == 0 || s.StopPrice <= 0) return;

        bool stopped = (pos.Quantity > 0 && bar.LowDouble <= s.StopPrice)
                    || (pos.Quantity < 0 && bar.HighDouble >= s.StopPrice);

        if (stopped)
        {
            _ctx.ClosePosition(s.InstrumentId);
            _ctx.Log($"止损: {s.InstrumentId} @ {s.StopPrice:F2}");
            s.EntryBarTime = null; s.StopPrice = 0; s.ActiveBi = null;
        }
    }

    private int CalculatePosition(double entryPrice, InstrumentState s, DirectionType dir)
    {
        var stopDistActual = StopAtrMult * s.CurrentAtr;
        if (stopDistActual < entryPrice * 0.005) return 0;

        var totalEquity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        var riskAmount = totalEquity * RiskPerTrade;
        int lots = (int)(riskAmount / (stopDistActual * s.ContractMultiplier));
        if (lots < 1) lots = 1;

        var marginPerLot = entryPrice * s.ContractMultiplier * s.MarginRate;
        var maxMargin = totalEquity * MaxMarginRatio;
        while (lots > 1 && marginPerLot * lots > maxMargin) lots--;
        if (lots < 1) return 0;
        return lots;
    }

    private static List<Bar> BuildDayBars(IReadOnlyList<Bar> history)
    {
        var dayBars = new List<Bar>();
        DateOnly? currentDay = null;
        Bar current = default;
        foreach (var bar in history)
        {
            var day = DateOnly.FromDateTime(bar.BarTime);
            if (day != currentDay)
            {
                if (currentDay != null) dayBars.Add(current);
                currentDay = day;
                current = new Bar { InstrumentId = bar.InstrumentId, TradingDay = bar.TradingDay,
                    BarTime = day.ToDateTime(TimeOnly.MinValue), Open = bar.Open, High = bar.High,
                    Low = bar.Low, Close = bar.Close, Volume = bar.Volume };
            }
            else
            {
                if (bar.High > current.High) current.High = bar.High;
                if (bar.Low < current.Low) current.Low = bar.Low;
                current.Close = bar.Close; current.Volume += bar.Volume;
            }
        }
        if (currentDay != null) dayBars.Add(current);
        return dayBars;
    }

    private class InstrumentState
    {
        public string InstrumentId;
        public double ContractMultiplier;
        public double MarginRate;
        public int AtrPeriod;
        public double StopAtrMult;
        public double MinBiPower;
        public int MinBiLen;

        public List<Bar> AllBars = [];  // 累计的全部 Bar（用于增量 Bi 检测）
        public List<Bi> DayBis = [];
        public List<Bi> All30mBis = [];
        public List<(DateTime EndTime, Bi Bi)> BiEndEvents = [];

        private readonly Queue<double> _trWindow;
        private double _trSum;
        public double CurrentAtr { get; private set; }
        private double _prevClose = double.NaN;

        public DateTime LastBarTime;
        public int NextBiEventIdx;
        public bool HasPendingEntry;
        public DateTime? EntryBarTime;
        public double StopPrice;
        public Bi? ActiveBi;

        public InstrumentState(string inst, double multiplier, double marginRate,
            int atrPeriod, double stopMult, double minPower, int minLen)
        {
            InstrumentId = inst;
            ContractMultiplier = multiplier;
            MarginRate = marginRate;
            AtrPeriod = atrPeriod;
            StopAtrMult = stopMult;
            MinBiPower = minPower;
            MinBiLen = minLen;
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
