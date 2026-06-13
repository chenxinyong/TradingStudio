using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;

namespace TradingStudio.Data.Engine;

/// <summary>
/// Tick 回放数据源 — K-way merge CSV，按时间顺序回放 TickRecord。
/// 同时通过 BarAggregator 合成 Bar → 产出 BarEvent。
/// 用于精确策略验证。
/// </summary>
public class HistoricalTickFeed : IDataFeed
{
    private readonly string _dataDir;
    private DateTime _startTime;
    private DateTime _endTime;
    private IReadOnlyList<string> _instruments = [];

    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;

    public HistoricalTickFeed(string dataDir)
    {
        _dataDir = dataDir;
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
        // Phase 2b 实现: K-way merge CSV
        await Task.CompletedTask;
        yield break;
    }
}
