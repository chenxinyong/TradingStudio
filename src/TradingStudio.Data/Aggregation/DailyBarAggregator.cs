using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingStudio.Core.Models;
using TradingStudio.Core.Models;

namespace TradingStudio.Data.Aggregation;

/// <summary>
/// 日线聚合器 — 实时更新的当日 K 线。
/// 每收到 Tick，更新当日的 Open/High/Low/Close/Volume/OI。
/// 交易日切换时发射上一日的 Bar。
/// </summary>
public class DailyBarAggregator : IDisposable
{
    private readonly ConcurrentDictionary<(string Inst, DateOnly Day), Bar> _bars = new();
    private readonly ConcurrentDictionary<(string Inst, DateOnly Day), object> _locks = new();
    private readonly ConcurrentDictionary<(string Inst, DateOnly Day), (long Vol, double TO)> _lastCum = new();
    private readonly Channel<Bar> _output = Channel.CreateBounded<Bar>(256);
    private bool _disposed;

    public ChannelReader<Bar> Output => _output.Reader;
    public event Action<Bar>? OnBar;

    /// <summary>喂入 Tick，实时更新当日日线</summary>
    public void Feed(TickRecord tick, string instrumentId, DateOnly tradingDay)
    {
        if (_disposed) return;

        var key = (instrumentId, tradingDay);
        var lk = _locks.GetOrAdd(key, _ => new object());
        lock (lk)
        {
            if (!_bars.TryGetValue(key, out var bar))
            {
                // 首 Tick：创建日线，Volume/Turnover=0（首 Tick 的增量从第二个 Tick 开始计算）
                bar = NewDay(tick, instrumentId, tradingDay);
                bar.Volume = 0;
                bar.Turnover = 0;
                _bars[key] = bar;
                _lastCum[key] = (tick.Volume, tick.Turnover);
                return;
            }

            // 后续 Tick：更新 OHLC + 成交量/额 delta
            if (tick.LastPrice > bar.High) bar.High = tick.LastPrice;
            if (tick.LastPrice < bar.Low) bar.Low = tick.LastPrice;
            bar.Close = tick.LastPrice;

            if (_lastCum.TryGetValue(key, out var last))
            {
                var dV = tick.Volume - last.Vol;
                if (dV > 0) bar.Volume += dV;
                var dTO = tick.Turnover - last.TO;
                if (dTO > 0) bar.Turnover += dTO;
            }
            _lastCum[key] = (tick.Volume, tick.Turnover);

            bar.OpenInterest = tick.OpenInterest;
            bar.TickCount++;

            // ⚠️ Bar 是 record struct，TryGetValue 返回副本，修改后必须写回
            _bars[key] = bar;
        }
    }

    /// <summary>获取当前日线快照（不发射）</summary>
    public Bar? GetSnapshot(string instrumentId, DateOnly tradingDay)
    {
        var key = (instrumentId, tradingDay);
        return _bars.TryGetValue(key, out var bar) ? bar : null;
    }

    /// <summary>发射并移除指定交易日的日线（交易日切换时调用）</summary>
    public void FlushDay(DateOnly tradingDay)
    {
        var keys = _bars.Keys.Where(k => k.Day < tradingDay).ToList();
        foreach (var key in keys)
        {
            if (_bars.TryRemove(key, out var bar))
            {
                _output.Writer.TryWrite(bar);
                OnBar?.Invoke(bar);
            }
        }
    }

    /// <summary>发射所有日线</summary>
    public void FlushAll()
    {
        foreach (var (key, bar) in _bars)
        {
            _output.Writer.TryWrite(bar);
            OnBar?.Invoke(bar);
        }
        _bars.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FlushAll();
        _output.Writer.Complete();
    }

    private static Bar NewDay(TickRecord tick, string instrumentId, DateOnly tradingDay) =>
        new()
        {
            InstrumentId = instrumentId,
            TradingDay = tradingDay,
            BarTime = tradingDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Open = tick.LastPrice,
            High = tick.LastPrice,
            Low = tick.LastPrice,
            Close = tick.LastPrice,
            Volume = 1,
            Turnover = tick.Turnover,
            OpenInterest = tick.OpenInterest,
            TickCount = 1,
        };
}
