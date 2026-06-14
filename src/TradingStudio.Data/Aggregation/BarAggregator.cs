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
            // 分钟切换：发射旧 Bar，创建空壳新 Bar
            if (state.HasBar && barTime != state.CurrentBarTime)
            {
                Emit(state.Current);
                state.Current = NewEmpty(instrumentId, tradingDay, barTime);
                state.CurrentBarTime = barTime;
            }
            // 首 Tick：创建空壳 Bar
            else if (!state.HasBar)
            {
                state.Current = NewEmpty(instrumentId, tradingDay, barTime);
                state.CurrentBarTime = barTime;
                state.HasBar = true;
            }

            // 每个 Tick 都累积 OHLC + 成交量/额 delta
            Accumulate(ref state.Current, tick, ref state.LastCumVolume, ref state.LastCumTurnover);
            state.LastTickTime = DateTime.UtcNow;
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

    private static Bar NewEmpty(string instrumentId, DateOnly tradingDay, DateTime barTime) =>
        new()
        {
            InstrumentId = instrumentId,
            TradingDay = tradingDay,
            BarTime = barTime,
            Open = 0,
            High = 0,
            Low = long.MaxValue,
            Close = 0,
            Volume = 0,
            Turnover = 0,
            OpenInterest = 0,
            TickCount = 0,
        };

    /// <summary>用 Tick 更新 Bar 的 OHLC + 成交量/额 delta</summary>
    private static void Accumulate(ref Bar bar, TickRecord tick,
        ref long lastCumVol, ref double lastCumTO)
    {
        var isFirst = bar.TickCount == 0;

        if (isFirst)
        {
            bar.Open = tick.LastPrice;
            bar.High = tick.LastPrice;
            bar.Low = tick.LastPrice;
        }
        else
        {
            if (tick.LastPrice > bar.High) bar.High = tick.LastPrice;
            if (tick.LastPrice < bar.Low) bar.Low = tick.LastPrice;
        }
        bar.Close = tick.LastPrice;

        // 成交量 delta = 本次累计 - 上次累计
        var dV = tick.Volume - lastCumVol;
        if (dV < 0) dV = tick.Volume;   // 跨交易日累计重置 → 直接用当日累计
        if (dV > 0) bar.Volume += dV;
        lastCumVol = tick.Volume;

        // 成交额 delta
        var dTO = tick.Turnover - lastCumTO;
        if (dTO < 0) dTO = tick.Turnover; // 跨交易日累计重置
        if (dTO > 0) bar.Turnover += dTO;
        lastCumTO = tick.Turnover;

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
        public long LastCumVolume;      // 上一 Tick 的累计成交量（用于计算 delta）
        public double LastCumTurnover;  // 上一 Tick 的累计成交额
        public void Reset() { HasBar = false; Current = default; }
    }
}
