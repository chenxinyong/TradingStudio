using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using TradingStudio.Data.Storage;

namespace TradingStudio.Data.Engine;

/// <summary>
/// Bar 回放数据源 — 从 SQLite bars_1min 读取，按需聚合为多周期 Bar。
/// periodMinutes=1 直接输出, 5/15/30/60 即时聚合, 不落地存储。
/// </summary>
public class HistoricalBarFeed : IDataFeed
{
    private readonly BarStore _store;
    private readonly int _periodMinutes;
    private DateTime _startTime;
    private DateTime _endTime;
    private IReadOnlyList<string> _instruments = [];

    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;

    /// <param name="store">BarStore 实例</param>
    /// <param name="periodMinutes">Bar 周期: 1=原始1min, 5/15/30/60=即时聚合</param>
    public HistoricalBarFeed(BarStore store, int periodMinutes = 1)
    {
        _store = store;
        _periodMinutes = periodMinutes;
    }

    /// <summary>预热用：预加载 1min Bar（聚合后返回）</summary>
    public async Task LoadBars(string instrumentId, DateTime start, DateTime end)
    {
        var bars = await _store.QueryBarsAsync(instrumentId, start, end, "bars_1min");
        if (_periodMinutes > 1)
            bars = new MultiBarAggregator(_periodMinutes).Aggregate(bars).ToList();
        _warmupBars.AddRange(bars);
    }

    public IReadOnlyList<Bar> GetWarmupBars(string instrumentId)
        => _warmupBars.Where(b => b.InstrumentId == instrumentId).ToList();

    private readonly List<Bar> _warmupBars = new();

    public void Initialize(DateTime startTime, DateTime endTime, IReadOnlyList<string> instruments)
    {
        _startTime = startTime;
        _endTime = endTime;
        _instruments = instruments;
    }

    public async IAsyncEnumerable<DataEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_instruments.Count == 0) yield break;

        var allBars = new List<Bar>();
        foreach (var inst in _instruments)
        {
            if (ct.IsCancellationRequested) yield break;
            var bars = await _store.QueryBarsAsync(inst, _startTime, _endTime, "bars_1min", ct);

            // 实时聚合
            if (_periodMinutes > 1)
                bars = new MultiBarAggregator(_periodMinutes).Aggregate(bars).ToList();

            allBars.AddRange(bars);
        }

        allBars.Sort((a, b) => a.BarTime.CompareTo(b.BarTime));

        DateTime? prevTime = null;
        foreach (var bar in allBars)
        {
            if (ct.IsCancellationRequested) yield break;
            var isNew = prevTime == null || bar.BarTime != prevTime.Value;
            yield return new BarEvent
            {
                Bar = bar,
                Time = new DateTimeOffset(bar.BarTime, TimeSpan.Zero),
                IsNewBar = isNew,
            };
            prevTime = bar.BarTime;
        }
    }
}
