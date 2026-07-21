using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
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
/// 同时将完成的 1min Bar 通过 SignalR 推送到监控客户端。
/// </summary>
public class LiveDataCollector : BackgroundService
{
    private readonly CtpLiveFeedV2 _feed;
    private readonly IBarStore _barStore;
    private readonly HealthMonitor _health;
    private readonly Serilog.ILogger _log;
    private readonly FutureRegistry _registry;
    private readonly HashSet<string> _top30Codes;
    private readonly IHubContext<EngineHub>? _hub;

    private long _tickCount;
    private long _barCount;
    private long _barsPushed;
    private readonly ConcurrentDictionary<string, string> _lastTickKey = new();

    private readonly TickCsvWriter? _tickWriter;

    public LiveDataCollector(CtpLiveFeedV2 feed, IBarStore barStore,
                             HealthMonitor health, Serilog.ILogger log,
                             FutureRegistry registry,
                             TickCsvWriter? tickWriter = null,
                             IHubContext<EngineHub>? hub = null)
    {
        _feed = feed;
        _barStore = barStore;
        _health = health;
        _log = log.ForContext<LiveDataCollector>();
        _registry = registry;
        _top30Codes = registry.Top30Codes;
        _tickWriter = tickWriter;
        _hub = hub;
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
                var pipeline = new QuotePipeline(_tickWriter!, _top30Codes, _registry);   // registry 启用聚合闸门
                barChannel = Channel.CreateBounded<Bar>(4096);

                pipeline.Agg1Min.OnBar += bar => {
                    Interlocked.Increment(ref _barCount);
                    barChannel.Writer.TryWrite(bar);
                    PushBarToSignalR(bar);
                };
                pipeline.AggDay.OnBar += bar => barChannel.Writer.TryWrite(bar);

                writeTask = WriteLoop(barChannel.Reader, ct);

                var reader = _feed.PersistChannel.Reader;
                await foreach (var item in reader.ReadAllAsync(ct))
                {
                    var (instId, tick, tradingDay) = item;

                    // 去重（基于 ExchangeTimestamp）
                    var tickKey = tick.ExchangeTimestamp.ToString();
                    if (_lastTickKey.TryGetValue(instId, out var prevKey) && prevKey == tickKey)
                        continue;
                    _lastTickKey[instId] = tickKey;

                    Interlocked.Increment(ref _tickCount);
                    pipeline.FeedTick(instId, tick, tradingDay);
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

        _log.Information("LiveDataCollector stopped — ticks={TickCount} bars={BarCount} pushed={Pushed}",
            _tickCount, _barCount, _barsPushed);
    }

    private void PushBarToSignalR(Bar bar)
    {
        if (_hub == null) return;
        var payload = new
        {
            InstrumentId = bar.InstrumentId,
            BarTime = bar.BarTime,
            Open = bar.OpenDouble,
            High = bar.HighDouble,
            Low = bar.LowDouble,
            Close = bar.CloseDouble,
            Volume = bar.Volume,
        };
        _ = _hub.Clients.Group($"bars:{bar.InstrumentId}").SendAsync("BarUpdated", payload);
        Interlocked.Increment(ref _barsPushed);
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
