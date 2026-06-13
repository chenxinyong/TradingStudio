using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;

namespace TradingStudio.Data.Engine;

/// <summary>
/// 实盘数据源 — 包装 CtpMdAdapter，产出统一 DataEvent 流。
/// Phase 3 实现。
/// </summary>
public class CtpLiveFeed : IDataFeed
{
    public IReadOnlyList<string> Instruments => throw new NotImplementedException();
    public DateTime StartTime => throw new NotImplementedException();
    public DateTime EndTime => throw new NotImplementedException();

    public void Initialize(DateTime startTime, DateTime endTime, IReadOnlyList<string> instruments)
    {
        throw new NotImplementedException("Phase 3: 实盘数据源");
    }

    public async IAsyncEnumerable<DataEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}
