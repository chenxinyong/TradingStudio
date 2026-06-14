using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Data.Storage;

namespace TradingStudio.Data.Engine;

/// <summary>
/// Bar 回放数据源 — 从 SQLite bars_1min 表按时间顺序回放 Bar。
/// 支持多品种 K-way merge，不产生 TickEvent。
/// </summary>
public class HistoricalBarFeed : IDataFeed
{
    private readonly BarStore _store;
    private readonly string _table;
    private DateTime _startTime;
    private DateTime _endTime;
    private IReadOnlyList<string> _instruments = [];

    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;

    public HistoricalBarFeed(BarStore store, string table = "bars_1min")
    {
        _store = store;
        _table = table;
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
        if (_instruments.Count == 0) yield break;

        // 1. 并行加载各品种 Bar
        var allBars = new List<Bar>();
        foreach (var inst in _instruments)
        {
            if (ct.IsCancellationRequested) yield break;
            var bars = await _store.QueryBarsAsync(inst, _startTime, _endTime, _table, ct);
            allBars.AddRange(bars);
        }

        // 2. 按 bar_time 排序（K-way merge 简化版：全量排序。品种 < 20 时性能可接受）
        allBars.Sort((a, b) => a.BarTime.CompareTo(b.BarTime));

        // 3. 产出 BarEvent 流
        DateTime? prevTime = null;
        foreach (var bar in allBars)
        {
            if (ct.IsCancellationRequested) yield break;
            var isNew = prevTime == null || bar.BarTime.Minute != prevTime.Value.Minute
                || bar.BarTime.Date != prevTime.Value.Date;
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
