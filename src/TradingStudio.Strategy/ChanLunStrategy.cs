using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;
using TradingStudio.Strategy.ChanLun;

namespace TradingStudio.Strategy;

/// <summary>
/// 缠论多级别策略 — 日线笔定方向 + 30min笔转折入场。
///
/// 核心逻辑:
///   日线 BI 向上 → 只做多, 在30min底分型(向下笔结束→向上笔开始)时入场
///   日线 BI 向下 → 只做空, 在30min顶分型(向上笔结束→向下笔开始)时入场
///   止损: ATR * 2 距离
///   出场: 反向BI转折信号 或 止损触发
///
/// 用法: Engine订阅30min Bar, 策略内部自建日线缠论分析。
/// </summary>
public class ChanLunStrategy : IStrategy
{
    [StrategyParameter(Description = "ATR周期", DefaultValue = 20, Min = 5, Max = 60, Category = "Risk")]
    public int AtrPeriod { get; set; } = 20;

    [StrategyParameter(Description = "止损ATR倍数", DefaultValue = 2, Min = 1, Max = 5, Category = "Risk")]
    public double StopAtrMult { get; set; } = 2.0;

    [StrategyParameter(Description = "最小笔力度(万分比)", DefaultValue = 200, Min = 50, Max = 500, Category = "Signal")]
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
    private double _contractMultiplier = 20.0;   // SA: 20吨/手
    private double _marginRate = 0.08;           // SA: 8%

    public void Initialize(StrategyContext context)
    {
        _ctx = context;

        foreach (var inst in context.SubscribedInstruments)
        {
            var future = context.GetFuture(inst);
            if (future != null)
            {
                _contractMultiplier = (double)future.TradingUnit;
                _marginRate = (double)future.MarginRate;
            }

            var history = context.GetBarHistory(inst);
            if (history.Count == 0)
            {
                context.LogWarning($"{inst}: 无历史数据，跳过");
                continue;
            }

            var state = new InstrumentState(inst, AtrPeriod, StopAtrMult, MinBiPower, MinBiLen);
            _state[inst] = state;

            // ── 1. 从历史30min Bar自建日线Bar ──
            var dayCoreBars = BuildDayBars(history);
            var dayChanlunBars = BarAdapter.FromCoreBars(dayCoreBars);
            var dayResult = ChanLunAnalyzer.Analyze(dayChanlunBars, minBiLen: 5);
            state.DayBis = dayResult.Bis;

            // ── 2. 对30min Bar运行缠论分析(一次性全量，用于预计算BI事件) ──
            var m30Bars = BarAdapter.FromCoreBars(history);
            var m30Result = ChanLunAnalyzer.Analyze(m30Bars, minBiLen: MinBiLen);
            state.All30mBis = m30Result.Bis;

            // ── 3. 预计算BI完成事件索引 ──
            // 每笔30min BI的结束时间→在history中的index
            foreach (var bi in m30Result.Bis)
            {
                var endDt = bi.DtEnd;
                // 找到 >= endDt 的第一个bar index
                int idx = FindBarIndex(history, endDt);
                if (idx >= 0)
                    state.BiEndIndexMap[idx] = bi;
            }

            // ── 4. 初始化ATR ──
            foreach (var bar in history)
                state.UpdateAtr(bar);

            context.Log($"{inst}: Day={dayResult.BiCount}BI, 30min={m30Result.BiCount}BI, ATR={state.CurrentAtr:F2}");
        }

        _ctx.Log($"初始化: {Name} on [{string.Join(", ", context.SubscribedInstruments)}]");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        // ── ATR更新 ──
        s.UpdateAtr(bar);

        // ── 检查日线BI方向(用最新的日线笔) ──
        // 注：ATR未就绪时自然跳过（CurrentAtr=0），无需显式warmup检查
        var dayDir = GetCurrentDayDirection(s, bar.BarTime);
        if (dayDir == DirectionType.Unknown)
            return; // 日线笔数据不足

        // ── 检查当前Bar是否触发30min BI完成事件 ──
        // Backtest engine中bar按时间顺序到达，bar index递增
        // 用当前bar时间查找预计算的BI事件
        if (s.LastBarIdx >= 0 && s.BiEndIndexMap.TryGetValue(s.LastBarIdx, out var completedBi))
        {
            // BI刚刚完成 — 检查是否满足入场条件
            ProcessBiCompletion(s, bar, completedBi, dayDir);
        }

        // ── 止损检查 ──
        CheckStopLoss(s, bar);

        s.LastBarIdx++;
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Filled)
            _ctx.Log($"成交: {evt.InstrumentId} {evt.Direction} {evt.Quantity}手 @ {evt.FillPrice:F2} [{evt.Message}]");

        if (evt.Type == OrderEventType.Rejected)
            _ctx.LogWarning($"拒单: {evt.InstrumentId} {evt.Message}");
    }

    public void OnEndOfAlgorithm()
    {
        foreach (var (inst, s) in _state)
        {
            var pos = _ctx.GetPosition(inst);
            _ctx.Log($"{inst}: Equity={_ctx.Equity:C}, Position={pos?.Quantity ?? 0}");
        }
    }

    // ═══════════════════════════════════════════════
    // 核心逻辑
    // ═══════════════════════════════════════════════

    private enum DirectionType { Up, Down, Unknown }

    /// <summary>获取当前时间点日线的BI方向</summary>
    private DirectionType GetCurrentDayDirection(InstrumentState s, DateTime barTime)
    {
        // 找到包含 barTime 的日线笔
        foreach (var bi in s.DayBis)
        {
            if (bi.DtStart <= barTime && barTime <= bi.DtEnd)
                return bi.Type == ChanLun.Direction.Up ? DirectionType.Up : DirectionType.Down;
        }
        // barTime在最后一笔之后 → 用最后笔的方向
        if (s.DayBis.Count > 0 && barTime > s.DayBis[^1].DtEnd)
            return s.DayBis[^1].Type == ChanLun.Direction.Up ? DirectionType.Up : DirectionType.Down;

        return DirectionType.Unknown;
    }

    /// <summary>处理30min BI完成信号</summary>
    private void ProcessBiCompletion(InstrumentState s, Bar bar, Bi completedBi, DirectionType dayDir)
    {
        // 跳过不满足质量的笔
        if (completedBi.Power < MinBiPower || completedBi.BarCount < MinBiLen)
            return;

        if (s.CurrentAtr <= 0)
            return;

        var pos = _ctx.GetPosition(s.InstrumentId);
        var hasPosition = pos is not null && pos.Quantity != 0;

        // ── 出场逻辑: 反向BI完成 → 平仓 ──
        if (hasPosition)
        {
            bool shouldExit = false;
            if (pos!.Quantity > 0 && completedBi.Type == ChanLun.Direction.Down)
                shouldExit = true;
            else if (pos.Quantity < 0 && completedBi.Type == ChanLun.Direction.Up)
                shouldExit = true;

            if (shouldExit)
            {
                _ctx.ClosePosition(s.InstrumentId);
                s.EntryBarTime = null;
                s.StopPrice = 0;
                s.ActiveBi = null;
                return;
            }
        }

        // ── 入场逻辑 (无持仓时) ──
        if (!hasPosition)
        {
            // 日线Up + 30min底分型(向下笔完成→向上笔开始) → 做多
            if (dayDir == DirectionType.Up && completedBi.Type == ChanLun.Direction.Up)
            {
                var qty = CalculatePosition(bar.CloseDouble, s, DirectionType.Up);
                if (qty > 0)
                {
                    _ctx.MarketBuy(s.InstrumentId, qty, $"BI入场: 日线Up+30min底分型");
                    s.EntryBarTime = bar.BarTime;
                    s.StopPrice = bar.CloseDouble - StopAtrMult * s.CurrentAtr;
                    s.ActiveBi = completedBi;
                }
            }
            // 日线Down + 30min顶分型(向上笔完成→向下笔开始) → 做空
            else if (dayDir == DirectionType.Down && completedBi.Type == ChanLun.Direction.Down)
            {
                var qty = CalculatePosition(bar.CloseDouble, s, DirectionType.Down);
                if (qty > 0)
                {
                    _ctx.MarketSell(s.InstrumentId, qty, $"BI入场: 日线Down+30min顶分型");
                    s.EntryBarTime = bar.BarTime;
                    s.StopPrice = bar.CloseDouble + StopAtrMult * s.CurrentAtr;
                    s.ActiveBi = completedBi;
                }
            }
        }
    }

    /// <summary>止损检查</summary>
    private void CheckStopLoss(InstrumentState s, Bar bar)
    {
        var pos = _ctx.GetPosition(s.InstrumentId);
        if (pos is null || pos.Quantity == 0 || s.StopPrice <= 0)
            return;

        bool stopped = false;
        if (pos.Quantity > 0 && bar.LowDouble <= s.StopPrice)
            stopped = true;
        else if (pos.Quantity < 0 && bar.HighDouble >= s.StopPrice)
            stopped = true;

        if (stopped)
        {
            _ctx.ClosePosition(s.InstrumentId);
            _ctx.Log($"止损: {s.InstrumentId} @ {s.StopPrice:F2}");
            s.EntryBarTime = null;
            s.StopPrice = 0;
            s.ActiveBi = null;
        }
    }

    // ═══════════════════════════════════════════════
    // 仓位计算
    // ═══════════════════════════════════════════════

    private int CalculatePosition(double entryPrice, InstrumentState s, DirectionType dir)
    {
        var stopDist = dir == DirectionType.Up
            ? StopAtrMult * s.CurrentAtr
            : StopAtrMult * s.CurrentAtr;
        // 止损距离 = StopAtrMult * ATR (价格单位)
        var stopDistActual = StopAtrMult * s.CurrentAtr;

        if (stopDistActual < entryPrice * 0.005) return 0; // 止损太近

        var totalEquity = (double)(_ctx.Equity > 0 ? _ctx.Equity : _ctx.AllocatedCapital);
        var riskAmount = totalEquity * RiskPerTrade;

        int lots = (int)(riskAmount / (stopDistActual * _contractMultiplier));
        if (lots < 1) lots = 1;

        // 保证金限制
        var marginPerLot = entryPrice * _contractMultiplier * _marginRate;
        var maxMargin = totalEquity * MaxMarginRatio;
        while (lots > 1 && marginPerLot * lots > maxMargin)
            lots--;

        if (lots < 1) return 0;

        return lots;
    }

    // ═══════════════════════════════════════════════
    // 工具方法
    // ═══════════════════════════════════════════════

    /// <summary>从 历史30min Bar 合成日线 Bar</summary>
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
                if (currentDay != null)
                    dayBars.Add(current);
                currentDay = day;
                current = new Bar
                {
                    InstrumentId = bar.InstrumentId,
                    TradingDay = bar.TradingDay,
                    BarTime = day.ToDateTime(TimeOnly.MinValue),
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume,
                };
            }
            else
            {
                if (bar.High > current.High) current.High = bar.High;
                if (bar.Low < current.Low) current.Low = bar.Low;
                current.Close = bar.Close;
                current.Volume += bar.Volume;
            }
        }
        if (currentDay != null) dayBars.Add(current);
        return dayBars;
    }

    /// <summary>找到 Bar 列表中 >= dt 的第一个索引</summary>
    private static int FindBarIndex(IReadOnlyList<Bar> bars, DateTime dt)
    {
        // 二分查找
        int lo = 0, hi = bars.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (bars[mid].BarTime < dt)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return lo < bars.Count ? lo : -1;
    }

    // ═══════════════════════════════════════════════
    // 内部状态
    // ═══════════════════════════════════════════════

    private class InstrumentState
    {
        public string InstrumentId;
        public int AtrPeriod;
        public double StopAtrMult;
        public double MinBiPower;
        public int MinBiLen;

        // 日线缠论结果
        public List<Bi> DayBis = [];

        // 30min预计算结果
        public List<Bi> All30mBis = [];
        public Dictionary<int, Bi> BiEndIndexMap = []; // barIdx → 刚完成的Bi

        // ATR
        private readonly Queue<double> _trWindow;
        private double _trSum;
        public double CurrentAtr { get; private set; }
        private double _prevClose = double.NaN;

        // 交易状态(实盘/回测共享)
        public int LastBarIdx = 0;
        public DateTime? EntryBarTime;
        public double StopPrice;
        public Bi? ActiveBi;

        public InstrumentState(string inst, int atrPeriod, double stopMult, double minPower, int minLen)
        {
            InstrumentId = inst;
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
                    Math.Max(Math.Abs(bar.HighDouble - _prevClose),
                             Math.Abs(bar.LowDouble - _prevClose)));
                _trWindow.Enqueue(tr);
                _trSum += tr;
                if (_trWindow.Count > AtrPeriod)
                    _trSum -= _trWindow.Dequeue();
                if (_trWindow.Count >= AtrPeriod)
                    CurrentAtr = _trSum / AtrPeriod;
            }
            _prevClose = bar.CloseDouble;
        }
    }
}
