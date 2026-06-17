using TradingStudio.Core.Models;

namespace TradingStudio.Core.Storage;

/// <summary>
/// Bar 持久化抽象。
/// 实现: SqliteBarStore (SQLite) / DuckDBStore (DuckDB)。
/// 回测和实盘通过此接口读写 Bar，不感知底层存储引擎。
/// </summary>
public interface IBarStore : IDisposable, IAsyncDisposable
{
    /// <summary>累计写入 Bar 数（线程安全）</summary>
    long WrittenCount { get; }

    /// <summary>异步写入单条 Bar（不阻塞调用线程）</summary>
    ValueTask WriteAsync(Bar bar);

    /// <summary>批量写入 Bar（带事务）</summary>
    ValueTask WriteBatchAsync(IEnumerable<Bar> bars, CancellationToken ct = default);

    /// <summary>查询指定品种和时间范围的 Bar，按 bar_time 升序</summary>
    Task<IReadOnlyList<Bar>> QueryBarsAsync(
        string instrumentId, DateTime start, DateTime end,
        string table = "bars_1min", CancellationToken ct = default);

    /// <summary>列出某表中所有不重复的品种 ID</summary>
    Task<IReadOnlyList<string>> QueryInstrumentsAsync(
        string table = "bars_1min", CancellationToken ct = default);

    /// <summary>从 bars_1min 聚合生成多周期表（如 bars_5min）</summary>
    Task BuildMultiPeriodTableAsync(int periodMinutes, CancellationToken ct = default);
}
