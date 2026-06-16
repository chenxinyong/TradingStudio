using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;

namespace TradingStudio.Live;

/// <summary>空数据源 — 不触发任何事件，用于隔离 CTP 问题排查。</summary>
public class NullDataFeed : IDataFeed
{
    public IReadOnlyList<string> Instruments { get; private set; } = [];
    public DateTime StartTime { get; private set; }
    public DateTime EndTime { get; private set; }
    public bool IsConnected => false;

    public void Initialize(DateTime startTime, DateTime endTime, IReadOnlyList<string> instruments)
    {
        StartTime = startTime;
        EndTime = endTime;
        Instruments = instruments;
    }

    public async IAsyncEnumerable<DataEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 永不产出事件——但也不崩溃，确认 Web + Engine 管线正常
        await Task.Delay(-1, ct);
        yield break;
    }
}
