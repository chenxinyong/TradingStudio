using System.Threading.Channels;
using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using TradingStudio.Data.Storage;
using TradingStudio.Live;

namespace TradingStudio.Services;

/// <summary>
/// Live 模式数据落盘 — 独立消费 CtpLiveFeed.PersistChannel，写入 BarStore。
/// 与 TradingEngine 并行运行，引擎崩溃不影响数据持久化。
/// </summary>
public class LiveDataCollector : BackgroundService
{
    private readonly CtpLiveFeed _feed;
    private readonly BarStore _barStore;
    private readonly HealthMonitor _health;
    private readonly ILogger<LiveDataCollector> _log;

    private long _tickCount;
    private long _barCount;

    private readonly TickCsvWriter? _tickWriter;

    public LiveDataCollector(CtpLiveFeed feed, BarStore barStore,
                             HealthMonitor health, ILogger<LiveDataCollector> log,
                             TickCsvWriter? tickWriter = null)
    {
        _feed = feed;
        _barStore = barStore;
        _health = health;
        _log = log;
        _tickWriter = tickWriter;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var barAgg = new BarAggregator();
        var dailyAgg = new DailyBarAggregator();
        var barChannel = Channel.CreateBounded<Bar>(4096);

        barAgg.OnBar += bar => { Interlocked.Increment(ref _barCount); barChannel.Writer.TryWrite(bar); };
        dailyAgg.OnBar += bar => barChannel.Writer.TryWrite(bar);

        // 后台写入 BarStore
        var writeTask = WriteLoop(barChannel.Reader, ct);

        try
        {
            var reader = _feed.PersistChannel.Reader;
            await foreach (var item in reader.ReadAllAsync(ct))
            {
                var (instId, tick, tradingDay) = item;
                Interlocked.Increment(ref _tickCount);
                _tickWriter?.WriteTick(tick, instId, tradingDay, "");
                barAgg.Feed(tick, instId, tradingDay);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error(ex, "LiveDataCollector stream error");
        }
        finally
        {
            barAgg.Flush();
            dailyAgg.Flush();
            barChannel.Writer.Complete();
            await writeTask;
        }

        _log.Information("LiveDataCollector stopped — ticks={TickCount} bars={BarCount}",
            _tickCount, _barCount);
    }

    private async Task WriteLoop(ChannelReader<Bar> reader, CancellationToken ct)
    {
        var batch = new List<Bar>(64);
        await foreach (var bar in reader.ReadAllAsync(ct))
        {
            batch.Add(bar);
            if (batch.Count >= 64)
            {
                await _barStore.WriteBatchAsync(batch, ct);
                batch.Clear();
                _health.Update("Running", _tickCount, _barCount, 0, 0,
                    null, null, null, DateTime.Now);
            }
        }
        if (batch.Count > 0)
            await _barStore.WriteBatchAsync(batch, ct);
    }
}
