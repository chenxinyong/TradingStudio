namespace TradingStudio.Core.Engine;

/// <summary>
/// 数据源接口 — 回测和实盘的统一抽象。
/// 回测模式: HistoricalTickFeed / HistoricalBarFeed
/// 实盘模式: CtpLiveFeed
/// </summary>
public interface IDataFeed
{
    IReadOnlyList<string> Instruments { get; }
    DateTime StartTime { get; }
    DateTime EndTime { get; }

    void Initialize(DateTime startTime, DateTime endTime, IReadOnlyList<string> instruments);

    /// <summary>统一事件流 —— Tick + Bar 混合产出</summary>
    IAsyncEnumerable<DataEvent> StreamAsync(CancellationToken ct);
}
