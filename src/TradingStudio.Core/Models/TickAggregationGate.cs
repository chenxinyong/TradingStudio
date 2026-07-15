using System.Collections.Concurrent;

namespace TradingStudio.Core.Models;

/// <summary>
/// Tick 进入 Bar 聚合前的两层闸门（只挡聚合路径，Tick CSV 保持全量原始落盘）：
///   1. 品种交易时段过滤 — 交易所 time-of-day 不在该品种 tradingHours 内（含竞价/收盘宽限）→ 拒
///      （品种未知 / 无 tradingHours / 解析失败 → 本层 fail-open 放行）
///   2. 陈旧快照过滤 — 交易所时间与本地接收时间偏差超阈值 → 拒（重连回推的旧快照）
///
/// 典型场景推演：
///   19:xx 盘前垃圾快照      → 第 1 层拒（所有品种盘外）
///   午休重连的 11:30 旧快照 → 第 1 层放行（收盘边界合法）→ 第 2 层拒（偏差 45min）
///   真实 11:30:00 收盘 tick → 两层都放行（本地延迟亚秒级）
///   20:59 集合竞价成交      → 第 1 层开盘宽限放行，第 2 层放行
/// </summary>
public sealed class TickAggregationGate
{
    private readonly FutureRegistry _registry;

    // 品种代码 → 解析结果缓存（null 也缓存 = fail-open）
    private readonly ConcurrentDictionary<string, TradingHoursSchedule?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private long _rejectedBySession;
    private long _rejectedAsStale;

    /// <summary>被时段层拒绝的 tick 数</summary>
    public long RejectedBySession => Interlocked.Read(ref _rejectedBySession);

    /// <summary>被陈旧层拒绝的 tick 数</summary>
    public long RejectedAsStale => Interlocked.Read(ref _rejectedAsStale);

    public TickAggregationGate(FutureRegistry registry) => _registry = registry;

    /// <summary>该 tick 是否应进入 Bar 聚合。线程安全。</summary>
    public bool ShouldAggregate(string instrumentId, TimeSpan exchangeTod, TimeSpan localBeijingTod)
    {
        // 第一层：品种交易时段
        var schedule = _cache.GetOrAdd(instrumentId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'),
            code => TradingHoursSchedule.TryParse(_registry.Find(code)?.TradingHours));
        if (schedule != null && !schedule.Contains(exchangeTod))
        {
            Interlocked.Increment(ref _rejectedBySession);
            return false;
        }

        // 第二层：陈旧快照（与品种无关，永远生效）
        if (StaleTickDetector.IsStale(exchangeTod, localBeijingTod))
        {
            Interlocked.Increment(ref _rejectedAsStale);
            return false;
        }

        return true;
    }
}
