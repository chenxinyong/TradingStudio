using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;
using TradingStudio.Data.Storage;

namespace TradingStudio.Data.Engine;

/// <summary>
/// Bar 回放数据源 — 从 SQLite bars_1min / bars_day 表按时间顺序回放 Bar。
/// 不产生 TickEvent，BarEvent.IsNewBar 始终为 true。
/// 用于快速筛选和参数粗调。
/// </summary>
public class HistoricalBarFeed : IDataFeed
{
    private readonly BarStore _store;
    private DateTime _startTime;
    private DateTime _endTime;
    private IReadOnlyList<string> _instruments = [];

    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;

    public HistoricalBarFeed(BarStore store)
    {
        _store = store;
    }

    public void Initialize(DateTime startTime, DateTime endTime, IReadOnlyList<string> instruments)
    {
        _startTime = startTime;
        _endTime = endTime;
        _instruments = instruments;
    }

    public async IAsyncEnumerable<DataEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Phase 2a 实现: 从 BarStore 读取 bars_1min，按时间排序产出 BarEvent
        await Task.CompletedTask;
        yield break;
    }
}
