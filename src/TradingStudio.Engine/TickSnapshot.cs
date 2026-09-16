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

    // 涨跌停价独立缓存：来自 CTP quote 的 UpperLimitPrice/LowerLimitPrice（交易所已按最小变动价位取整）。
    // 当日恒定（开盘由昨结算价确定），与 LastPrice 的更新时序解耦，因此单独旁路写入。
    private readonly ConcurrentDictionary<string, (double Upper, double Lower)> _limits = new();

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

    /// <summary>更新一个品种的涨跌停价（行情侧写入，交易所已按 tick 取整的权威价）</summary>
    public void UpdateLimits(string instrumentId, double upper, double lower) =>
        _limits[instrumentId] = (upper, lower);

    /// <summary>读取涨跌停价；尚未更新时返回 false 且 upper/lower=0</summary>
    public bool TryGetLimits(string instrumentId, out double upper, out double lower)
    {
        if (_limits.TryGetValue(instrumentId, out var l)) { upper = l.Upper; lower = l.Lower; return true; }
        upper = lower = 0; return false;
    }

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
