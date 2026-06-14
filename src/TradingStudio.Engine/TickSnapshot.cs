using System.Collections.Concurrent;
using TradingStudio.Core.Models;

namespace TradingStudio.Engine;

/// <summary>
/// 全品种最新 Tick 快照 — WatchList / 行情监控的数据源。
/// 每个 TickEvent 到达时更新，API 只读查询。
/// </summary>
public class TickSnapshot
{
    private readonly ConcurrentDictionary<string, TickSnapshotItem> _ticks = new();

    /// <summary>更新一个品种的最新 Tick</summary>
    public void Update(string instrumentId, TickRecord tick, DateTimeOffset time)
    {
        _ticks[instrumentId] = new TickSnapshotItem
        {
            InstrumentId = instrumentId,
            LastPrice = (double)tick.LastPrice / TickRecord.PriceScale,
            Volume = tick.Volume,
            BidPrice1 = (double)tick.BidPrice1 / TickRecord.PriceScale,
            AskPrice1 = (double)tick.AskPrice1 / TickRecord.PriceScale,
            OpenInterest = tick.OpenInterest,
            UpdateTime = time,
        };
    }

    /// <summary>获取单个品种快照</summary>
    public TickSnapshotItem? Get(string instrumentId) =>
        _ticks.TryGetValue(instrumentId, out var t) ? t : null;

    /// <summary>获取全部快照</summary>
    public IReadOnlyList<TickSnapshotItem> GetAll() =>
        _ticks.Values.OrderBy(t => t.InstrumentId).ToList();

    /// <summary>获取订阅品种数</summary>
    public int Count => _ticks.Count;
}

public class TickSnapshotItem
{
    public string InstrumentId { get; init; } = "";
    public double LastPrice { get; init; }
    public long Volume { get; init; }
    public double BidPrice1 { get; init; }
    public double AskPrice1 { get; init; }
    public double OpenInterest { get; init; }
    public DateTimeOffset UpdateTime { get; init; }
}
