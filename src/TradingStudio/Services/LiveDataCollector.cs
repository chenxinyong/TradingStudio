using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingStudio.Core.Models;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Aggregation;
using TradingStudio.Data.Storage;
using TradingStudio.Live;
using Serilog;

namespace TradingStudio.Services;

/// <summary>
/// Live 模式数据落盘 — 独立消费 CtpLiveFeed.PersistChannel，写入 IBarStore。
/// 与 TradingEngine 并行运行，引擎崩溃不影响数据持久化。
/// </summary>
public class LiveDataCollector : BackgroundService
{
    private readonly CtpLiveFeed _feed;
    private readonly IBarStore _barStore;
    private readonly HealthMonitor _health;
    private readonly Serilog.ILogger _log;
    private readonly FutureRegistry _registry;
    private readonly HashSet<string> _top30Codes;

    private long _tickCount;
    private long _barCount;
    private readonly ConcurrentDictionary<string, string> _lastTickKey = new(); // 去重：instId → "UpdateTime_Millisec"

    private readonly TickCsvWriter? _tickWriter;

    public LiveDataCollector(CtpLiveFeed feed, IBarStore barStore,
                             HealthMonitor health, Serilog.ILogger log,
                             FutureRegistry registry,
                             TickCsvWriter? tickWriter = null)
    {
        _feed = feed;
        _barStore = barStore;
        _health = health;
        _log = log.ForContext<LiveDataCollector>();
        _registry = registry;
        _top30Codes = registry.Top30Codes;
        _tickWriter = tickWriter;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("LiveDataCollector starting...");

        while (!ct.IsCancellationRequested)
        {
            Channel<Bar>? barChannel = null;
            Task? writeTask = null;

            try
            {
                var pipeline = new QuotePipeline(_tickWriter!, _top30Codes);
                barChannel = Channel.CreateBounded<Bar>(4096);

                pipeline.Agg1Min.OnBar += bar => { Interlocked.Increment(ref _barCount); barChannel.Writer.TryWrite(bar); };
                pipeline.AggDay.OnBar += bar => barChannel.Writer.TryWrite(bar);

                writeTask = WriteLoop(barChannel.Reader, ct);

                var reader = _feed.PersistChannel.Reader;
                await foreach (var item in reader.ReadAllAsync(ct))
                {
                    var (instId, quote, tradingDay) = item;

                    // 去重（Live 特有：CTP 可能推送重复快照）
                    var tickKey = $"{quote.UpdateTime}_{quote.UpdateMillisec}";
                    if (_lastTickKey.TryGetValue(instId, out var prevKey) && prevKey == tickKey)
                        continue;
                    _lastTickKey[instId] = tickKey;

                    Interlocked.Increment(ref _tickCount);
                    pipeline.Feed(quote);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _log.Information("LiveDataCollector cancelled (shutdown requested)");
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "LiveDataCollector crashed - retrying in 10s...");
                try { await Task.Delay(10_000, ct); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                try
                {
                    barChannel?.Writer.Complete();
                    if (writeTask != null) await writeTask;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Error during LiveDataCollector cleanup");
                }
            }
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
