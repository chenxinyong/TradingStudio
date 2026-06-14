using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Strategy;

namespace TradingStudio.Engine.Examples;

/// <summary>双均线突破策略 — 支持多品种，每品种独立窗口</summary>
public class MaCrossStrategy : IStrategy
{
    [StrategyParameter(Description = "快线周期", DefaultValue = 5, Min = 2, Max = 60, Category = "Entry")]
    public int FastPeriod { get; set; } = 5;

    [StrategyParameter(Description = "慢线周期", DefaultValue = 20, Min = 5, Max = 200, Category = "Entry")]
    public int SlowPeriod { get; set; } = 20;

    [StrategyParameter(Description = "每笔交易手数", DefaultValue = 1, Min = 1, Max = 100, Category = "Position")]
    public int Quantity { get; set; } = 1;

    public string Name => "MA双均线突破";

    private StrategyContext _ctx = null!;
    private Dictionary<string, InstrumentState> _state = new();

    public void Initialize(StrategyContext context)
    {
        _ctx = context;
        foreach (var inst in context.SubscribedInstruments)
            _state[inst] = new InstrumentState(FastPeriod, SlowPeriod);
        _ctx.Log($"初始化: {Name} on [{string.Join(", ", context.SubscribedInstruments)}] (快线={FastPeriod}, 慢线={SlowPeriod})");
    }

    public void OnTick(TickRecord tick, string instrumentId) { /* Phase 2b */ }

    public void OnBar(Bar bar)
    {
        if (!_state.TryGetValue(bar.InstrumentId, out var s)) return;

        var price = bar.CloseDouble;

        // 滑动窗口 SMA
        s.Fast.Enqueue(price); s.FastSum += price;
        if (s.Fast.Count > FastPeriod) s.FastSum -= s.Fast.Dequeue();
        s.Slow.Enqueue(price); s.SlowSum += price;
        if (s.Slow.Count > SlowPeriod) s.SlowSum -= s.Slow.Dequeue();

        if (s.Fast.Count < FastPeriod || s.Slow.Count < SlowPeriod) return;

        var fastMa = s.FastSum / FastPeriod;
        var slowMa = s.SlowSum / SlowPeriod;

        if (double.IsNaN(s.PrevFast)) { s.PrevFast = fastMa; s.PrevSlow = slowMa; return; }

        var pos = _ctx.GetPosition(bar.InstrumentId);
        var hasLong = pos is not null && pos.Quantity > 0;
        var hasShort = pos is not null && pos.Quantity < 0;

        // 预热模式：只更新 SMA，不产生交易信号
        if (_ctx is Engine.EngineStrategyContext engCtx && engCtx.IsWarmup)
        {
            s.PrevFast = fastMa;
            s.PrevSlow = slowMa;
            return;
        }

        // 金叉做多
        if (s.PrevFast <= s.PrevSlow && fastMa > slowMa && !hasLong)
        {
            if (hasShort) _ctx.ClosePosition(bar.InstrumentId);
            _ctx.MarketBuy(bar.InstrumentId, Quantity, "金叉做多");
        }
        // 死叉做空
        else if (s.PrevFast >= s.PrevSlow && fastMa < slowMa && !hasShort)
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
        else if (evt.Type == OrderEventType.Rejected)
            _ctx.LogError($"拒绝: {evt.Message}");
    }

    public void OnEndOfAlgorithm()
    {
        _ctx.Log($"回测结束. 最终权益: {_ctx.Equity:C}");
    }

    /// <summary>
    /// 每个品种的状态：维护快线、慢线的滑动窗口和当前值，以及上一个 Bar 的快慢线值用于判断交叉。
    /// </summary>
    private class InstrumentState
    {
        public Queue<double> Fast, Slow;
        public double FastSum, SlowSum;
        public double PrevFast = double.NaN, PrevSlow = double.NaN;

        public InstrumentState(int fastPeriod, int slowPeriod)
        {
            Fast = new Queue<double>(fastPeriod + 1);
            Slow = new Queue<double>(slowPeriod + 1);
        }
    }
}
