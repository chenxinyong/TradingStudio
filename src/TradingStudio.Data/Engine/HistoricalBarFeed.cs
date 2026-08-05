using System.Runtime.CompilerServices;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Aggregation;

namespace TradingStudio.Data.Engine;

/// <summary>
/// Bar 回放数据源 — 从 IBarStore 读取，按需聚合。
/// 单品种直接流式输出，多品种 K-way merge 排序。
/// </summary>
public class HistoricalBarFeed : IDataFeed
{
    private readonly IBarStore _store;
    private readonly int _periodMinutes;
    private readonly string _barTable;
    private DateTime _startTime;
    private DateTime _endTime;
    private IReadOnlyList<string> _instruments = [];

    public IReadOnlyList<string> Instruments => _instruments;
    public DateTime StartTime => _startTime;
    public DateTime EndTime => _endTime;

    public HistoricalBarFeed(IBarStore store, int periodMinutes = 1, string barTable = "bars_1min")
    {
        _store = store;
        _periodMinutes = periodMinutes;
        _barTable = barTable;
    }

    public async Task LoadBars(string instrumentId, DateTime start, DateTime end)
    {
        var bars = await _store.QueryBarsAsync(instrumentId, start, end, _barTable);
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

        if (_instruments.Count == 1)
        {
            var bars = await _store.QueryBarsAsync(_instruments[0], _startTime, _endTime, _barTable, ct);
            if (_periodMinutes > 1) bars = MultiAggregate(bars);

            DateTime? prevTime = null;
            foreach (var bar in bars)
            {
                if (ct.IsCancellationRequested) yield break;
                if (IsWeekend(bar.BarTime)) continue;  // 跳过周末
                var isNew = prevTime == null || bar.BarTime != prevTime.Value;
                yield return new BarEvent { Bar = bar, Time = new DateTimeOffset(bar.BarTime, TimeSpan.Zero), IsNewBar = isNew };
                prevTime = bar.BarTime;
            }
        }
        else
        {
            // 多品种并行加载 → K-way merge
            var loadTasks = _instruments.Select(async inst =>
            {
                var raw = await _store.QueryBarsAsync(inst, _startTime, _endTime, _barTable, ct);
                var bars = _periodMinutes > 1 ? MultiAggregate(raw) : raw.ToList();
                return (inst, bars);
            });
            var results = await Task.WhenAll(loadTasks);

            var lists = new List<List<Bar>>();
            var indices = new List<int>();
            foreach (var (_, bars) in results)
            {
                if (bars.Count > 0) { lists.Add(bars); indices.Add(0); }
            }

            var prevTimes = new Dictionary<string, DateTime>();  // 按品种跟踪
            while (true)
            {
                int minIdx = -1;
                DateTime minTime = DateTime.MaxValue;
                for (int i = 0; i < lists.Count; i++)
                {
                    if (indices[i] < lists[i].Count && lists[i][indices[i]].BarTime < minTime)
                    {
                        minTime = lists[i][indices[i]].BarTime;
                        minIdx = i;
                    }
                }
                if (minIdx < 0) break;
                if (ct.IsCancellationRequested) yield break;

                var bar = lists[minIdx][indices[minIdx]];
                indices[minIdx]++;

                if (IsWeekend(bar.BarTime)) continue;

                // 品种内 IsNewBar：该品种自身上一根 Bar 的时间不同
                var instId = bar.InstrumentId;
                var isNew = !prevTimes.TryGetValue(instId, out var pt)
                    || bar.BarTime != pt;
                prevTimes[instId] = bar.BarTime;

                yield return new BarEvent { Bar = bar, Time = new DateTimeOffset(bar.BarTime, TimeSpan.Zero), IsNewBar = isNew };
            }
            }
        }

    private List<Bar> MultiAggregate(IReadOnlyList<Bar> bars)
        => new MultiBarAggregator(_periodMinutes).Aggregate(bars).ToList();

    /// <summary>是否为周末（周六/周日不交易）</summary>
    private static bool IsWeekend(DateTime dt)
        => dt.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
