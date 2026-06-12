using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingStudio.Core.Models;
using TradingStudio.Core.Models;

namespace TradingStudio.Data.Aggregation;

/// <summary>
/// Bar 聚合器 — TickRecord 流 → 1分钟 Bar。
/// 按 InstrumentID 分组，每个品种独立维护当前 Bar。
/// 分钟切换时发射上一分钟 Bar，30 秒无数据兜底。
/// </summary>
public class BarAggregator : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, InstrumentState> _states = new();
    private readonly Channel<Bar> _output = Channel.CreateBounded<Bar>(1024);
    private readonly Timer _tickTimer;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private bool _disposed;

    public ChannelReader<Bar> Output => _output.Reader;

    public event Action<Bar>? OnBar;
    public event Action<string>? OnMinuteChange; // instrumentId that switched minute

    public BarAggregator()
    {
        _tickTimer = new Timer(CheckTimeouts, null, 5000, 5000);
    }

    /// <summary>喂入一条 TickRecord</summary>
    public void Feed(TickRecord tick, string instrumentId, DateOnly tradingDay)
    {
        if (_disposed) return;

        var barTime = RoundToMinute(tick.ExchangeTime);
        var state = _states.GetOrAdd(instrumentId, _ => new InstrumentState());

        lock (state)
        {
            // 同一分钟 → 累积
            if (barTime == state.CurrentBarTime)
            {
                Accumulate(ref state.Current, tick, barTime);
                state.LastTickTime = DateTime.UtcNow;
                return;
            }

            // 分钟切换 → 发射旧 Bar，开始新 Bar
            if (state.HasBar)
                Emit(state.Current);

            state.Current = NewBar(tick, instrumentId, tradingDay, barTime);
            state.CurrentBarTime = barTime;
            state.LastTickTime = DateTime.UtcNow;
            state.HasBar = true;
        }
    }

    /// <summary>强制 flush 所有品种当前 Bar</summary>
    public void Flush()
    {
        foreach (var (_, state) in _states)
        {
            lock (state)
            {
                if (state.HasBar)
                {
                    Emit(state.Current);
                    state.Reset();
                }
            }
        }
    }

    private void CheckTimeouts(object? _)
    {
        var now = DateTime.UtcNow;
        foreach (var (_, state) in _states)
        {
            lock (state)
            {
                if (state.HasBar && (now - state.LastTickTime) > _timeout)
                {
                    Emit(state.Current);
                    state.Reset();
                }
            }
        }
    }

    private void Emit(Bar bar)
    {
        _output.Writer.TryWrite(bar);
        OnBar?.Invoke(bar);
    }

    // === helpers ===

    private static DateTime RoundToMinute(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);

    private static Bar NewBar(TickRecord tick, string instrumentId, DateOnly tradingDay, DateTime barTime) =>
        new()
        {
            InstrumentId = instrumentId,
            TradingDay = tradingDay,
            BarTime = barTime,
            Open = tick.LastPrice,
            High = tick.LastPrice,
            Low = tick.LastPrice,
            Close = tick.LastPrice,
            Volume = 0,
            Turnover = 0,
            OpenInterest = tick.OpenInterest,
            TickCount = 1,
        };

    private static void Accumulate(ref Bar bar, TickRecord tick, DateTime barTime)
    {
        if (tick.LastPrice > bar.High) bar.High = tick.LastPrice;
        if (tick.LastPrice < bar.Low) bar.Low = tick.LastPrice;
        bar.Close = tick.LastPrice;
        bar.Volume++;
        bar.Turnover += 0;  // CTP Turnover is cumulative, delta tracked separately
        bar.OpenInterest = tick.OpenInterest;
        bar.TickCount++;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tickTimer.Dispose();
        Flush();
        _output.Writer.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }

    private sealed class InstrumentState
    {
        public Bar Current;
        public DateTime CurrentBarTime;
        public DateTime LastTickTime;
        public bool HasBar;
        public void Reset() { HasBar = false; Current = default; }
    }
}
