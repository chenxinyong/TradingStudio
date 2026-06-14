using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>
/// 双均线多周期策略 — 1min 入场 + 日线趋势过滤。
/// 日线 SMA(20) 向上 → 只做多；向下 → 只做空。1min 金叉/死叉 入场。
/// </summary>
public class MaCrossMultiTfStrategy : IStrategy
{
    [StrategyParameter(Description = "快线周期", DefaultValue = 5, Min = 2, Max = 60, Category = "Entry")]
    public int FastPeriod { get; set; } = 5;

    [StrategyParameter(Description = "慢线周期", DefaultValue = 20, Min = 5, Max = 200, Category = "Entry")]
    public int SlowPeriod { get; set; } = 20;

    [StrategyParameter(Description = "日线趋势周期", DefaultValue = 20, Min = 5, Max = 60, Category = "Trend")]
    public int DayTrendPeriod { get; set; } = 20;

    [StrategyParameter(Description = "每笔交易手数", DefaultValue = 1, Min = 1, Max = 100, Category = "Position")]
    public int Quantity { get; set; } = 1;

    public string Name => "MA双均线多周期";

    private StrategyContext _ctx = null!;
    private readonly Dictionary<string, InstrumentState> _state = new();

    // 日线趋势
    private readonly Dictionary<string, DayTrend> _dayTrends = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        foreach (var inst in context.SubscribedInstruments)
        {
            _state[inst] = new InstrumentState(FastPeriod, SlowPeriod);

            // 从历史数据自建日线趋势
            var history = context.GetBarHistory(inst);
            BuildDayTrend(inst, history);
        }
        _ctx.Log($"初始化: {Name} on [{string.Join(", ", context.SubscribedInstruments)}] (1min={FastPeriod}/{SlowPeriod}, 日线={DayTrendPeriod})");
    }

    public void OnTick(TickRecord tick, string instrumentId) { }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        // ── 1min SMA ──
        var price = bar.CloseDouble;
        s.Fast.Enqueue(price); s.FastSum += price;
        if (s.Fast.Count > FastPeriod) s.FastSum -= s.Fast.Dequeue();
        s.Slow.Enqueue(price); s.SlowSum += price;
        if (s.Slow.Count > SlowPeriod) s.SlowSum -= s.Slow.Dequeue();
        if (s.Fast.Count < FastPeriod || s.Slow.Count < SlowPeriod) return;

        var fastMa = s.FastSum / FastPeriod;
        var slowMa = s.SlowSum / SlowPeriod;
        if (double.IsNaN(s.PrevFast)) { s.PrevFast = fastMa; s.PrevSlow = slowMa; return; }

        // ── 日线趋势更新 ──
        UpdateDayTrend(bar);

        // ── 交易信号 ──
        if (_ctx is EngineStrategyContext engCtx && engCtx.IsWarmup)
        {
            s.PrevFast = fastMa; s.PrevSlow = slowMa;
            return;
        }

        var pos = _ctx.GetPosition(bar.InstrumentId);
        var hasLong = pos is not null && pos.Quantity > 0;
        var hasShort = pos is not null && pos.Quantity < 0;

        // 日线趋势方向
        var trend = _dayTrends.GetValueOrDefault(bar.InstrumentId)?.Direction ?? TrendDirection.Neutral;

        // 金叉 + 日线向上 → 做多
        if (s.PrevFast <= s.PrevSlow && fastMa > slowMa && !hasLong && trend != TrendDirection.Down)
        {
            if (hasShort) _ctx.ClosePosition(bar.InstrumentId);
            _ctx.MarketBuy(bar.InstrumentId, Quantity, "金叉做多");
        }
        // 死叉 + 日线向下 → 做空
        else if (s.PrevFast >= s.PrevSlow && fastMa < slowMa && !hasShort && trend != TrendDirection.Up)
        {
            if (hasLong) _ctx.ClosePosition(bar.InstrumentId);
            _ctx.MarketSell(bar.InstrumentId, Quantity, "死叉做空");
        }

        s.PrevFast = fastMa;
        s.PrevSlow = slowMa;
    }

    public void OnOrderEvent(OrderEvent evt)
    {
        if (evt.Type == OrderEventType.Filled)
            _ctx.Log($"成交: {evt.InstrumentId} {evt.Direction} {evt.Quantity}手 @ {evt.FillPrice:F2}");
    }

    public void OnEndOfAlgorithm()
    {
        _ctx.Log($"回测结束. 最终权益: {_ctx.Equity:C}");
    }

    // ── 日线自聚合 ──

    /// <summary>从历史 Bar 自建日线趋势</summary>
    private void BuildDayTrend(string inst, IReadOnlyList<Bar> history)
    {
        var dayBars = new List<Bar>();
        DateOnly? currentDay = null;
        Bar currentDayBar = default;

        foreach (var bar in history.Where(b => b.InstrumentId == inst))
        {
            var day = DateOnly.FromDateTime(bar.BarTime);
            if (day != currentDay)
            {
                if (currentDay != null)
                    dayBars.Add(currentDayBar);
                currentDay = day;
                currentDayBar = new Bar
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
                if (bar.High > currentDayBar.High) currentDayBar.High = bar.High;
                if (bar.Low < currentDayBar.Low) currentDayBar.Low = bar.Low;
                currentDayBar.Close = bar.Close;
                currentDayBar.Volume += bar.Volume;
            }
        }
        if (currentDay != null) dayBars.Add(currentDayBar);

        // 计算日线 SMA 趋势
        var trend = new DayTrend(DayTrendPeriod);
        foreach (var dbar in dayBars)
            trend.Update(dbar.CloseDouble);
        _dayTrends[inst] = trend;
        _ctx.Log($"  {inst} 日线趋势: {dayBars.Count} days, SMA({DayTrendPeriod})={trend.CurrentMa:F2}");
    }

    /// <summary>实时更新日线趋势——每天收盘后更新一次</summary>
    private DateOnly _lastDayUpdate;
    private void UpdateDayTrend(Bar bar)
    {
        var day = DateOnly.FromDateTime(bar.BarTime);
        if (day == _lastDayUpdate) return;
        _lastDayUpdate = day;

        // 用当天最后一根 1min Bar 的收盘价作为日线收盘价
        if (_dayTrends.TryGetValue(bar.InstrumentId, out var trend))
            trend.Update(bar.CloseDouble);
    }

    private class DayTrend
    {
        private readonly int _period;
        private readonly Queue<double> _window = new();
        private double _sum;
        public double CurrentMa { get; private set; } = double.NaN;
        public TrendDirection Direction { get; private set; } = TrendDirection.Neutral;
        private double _prevMa = double.NaN;

        public DayTrend(int period) { _period = period; }

        public void Update(double price)
        {
            _window.Enqueue(price); _sum += price;
            if (_window.Count > _period) _sum -= _window.Dequeue();
            if (_window.Count < _period) return;

            _prevMa = CurrentMa;
            CurrentMa = _sum / _period;
            Direction = double.IsNaN(_prevMa) ? TrendDirection.Neutral
                : CurrentMa > _prevMa ? TrendDirection.Up
                : CurrentMa < _prevMa ? TrendDirection.Down
                : TrendDirection.Neutral;
        }
    }

    private enum TrendDirection { Up, Down, Neutral }

    private class InstrumentState
    {
        public Queue<double> Fast, Slow;
        public double FastSum, SlowSum;
        public double PrevFast = double.NaN, PrevSlow = double.NaN;
        public InstrumentState(int fast, int slow) { Fast = new(fast + 1); Slow = new(slow + 1); }
    }
}
