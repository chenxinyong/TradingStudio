using TradingStudio.Core.Models;

namespace TradingStudio.Core.Storage;

/// <summary>
/// Tick 持久化抽象 — 短期滚动窗口存储。
/// 目前仅 DuckDBStore 实现此接口（7天自动清理）。
/// SQLite 方案仍用 TickCsvWriter 写 CSV，不实现此接口。
/// </summary>
public interface ITickStore
{
    /// <summary>累计写入 Tick 数（线程安全）</summary>
    long TickCount { get; }

    /// <summary>异步写入单条 Tick</summary>
    ValueTask WriteTickAsync(TickRecord tick, string symbol);

    /// <summary>批量写入 Tick（Appender API，高性能）</summary>
    ValueTask WriteTicksBatchAsync(IEnumerable<TickRecord> ticks, string symbol, CancellationToken ct = default);

    /// <summary>查询指定品种和时间范围的 Tick</summary>
    Task<IReadOnlyList<TickRecord>> QueryTicksAsync(
        string symbol, DateTime start, DateTime end, CancellationToken ct = default);

    /// <summary>清理超过保留天数的 Tick。返回删除行数。</summary>
    Task<int> PurgeOldTicksAsync(int retentionDays = 7, CancellationToken ct = default);
}
