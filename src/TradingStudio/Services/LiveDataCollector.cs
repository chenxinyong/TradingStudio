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
    private long _tickSkipped; // 非 Top 30 跳过的 Tick 数
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
            BarAggregator? barAgg = null;
            DailyBarAggregator? dailyAgg = null;
            Channel<Bar>? barChannel = null;
            Task? writeTask = null;

            try
            {
                barAgg = new BarAggregator();
                dailyAgg = new DailyBarAggregator();
                barChannel = Channel.CreateBounded<Bar>(4096);

                barAgg.OnBar += bar => { Interlocked.Increment(ref _barCount); barChannel.Writer.TryWrite(bar); };
                dailyAgg.OnBar += bar => barChannel.Writer.TryWrite(bar);

                // 后台写入 BarStore
                writeTask = WriteLoop(barChannel.Reader, ct);

                var reader = _feed.PersistChannel.Reader;
                await foreach (var item in reader.ReadAllAsync(ct))
                {
                    var (instId, quote, tradingDay) = item;

                    // 去重：同一 UpdateTime+Millisec 的重复快照只写一次
                    var tickKey = $"{quote.UpdateTime}_{quote.UpdateMillisec}";
                    if (_lastTickKey.TryGetValue(instId, out var prevKey) && prevKey == tickKey)
                        continue;  // 重复快照，跳过
                    _lastTickKey[instId] = tickKey;

                    Interlocked.Increment(ref _tickCount);

                    // Phase 3 数据分层：仅 Top 30 品种写 Tick CSV
                    // 其余品种：1min + Day Bar 照常聚合，仅跳过 Tick 落盘
                    var productCode = _registry.Resolve(instId)?.Code;
                    var isTop30 = productCode != null && _top30Codes.Contains(productCode);

                    if (isTop30)
                    {
                        _tickWriter?.Write(
                            instId,
                            string.IsNullOrEmpty(quote.ExchangeID) ? TickCsvWriter.GuessExchange(instId) : quote.ExchangeID,
                            tradingDay.ToString("yyyyMMdd"),
                            quote.UpdateTime ?? "", quote.UpdateMillisec,
                            quote.LastPrice, quote.PreSettlementPrice, quote.PreClosePrice, quote.PreOpenInterest,
                            quote.OpenPrice, quote.HighestPrice, quote.LowestPrice,
                            quote.Volume, quote.Turnover, quote.OpenInterest,
                            quote.ClosePrice, quote.SettlementPrice, quote.UpperLimitPrice, quote.LowerLimitPrice,
                            quote.BidPrice1, quote.BidVolume1, quote.AskPrice1, quote.AskVolume1,
                            quote.BidPrice2, quote.BidVolume2, quote.AskPrice2, quote.AskVolume2,
                            quote.BidPrice3, quote.BidVolume3, quote.AskPrice3, quote.AskVolume3,
                            quote.BidPrice4, quote.BidVolume4, quote.AskPrice4, quote.AskVolume4,
                            quote.BidPrice5, quote.BidVolume5, quote.AskPrice5, quote.AskVolume5,
                            quote.AveragePrice);
                    }
                    else
                    {
                        Interlocked.Increment(ref _tickSkipped);
                    }

                    // 同时喂 1min + Day Bar 聚合器
                    var record = QuoteConverter.FromCTPQuote(quote);
                    barAgg.Feed(record, instId, tradingDay);
                    dailyAgg.Feed(record, instId, tradingDay);
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
                // 清理资源
                try
                {
                    barAgg?.Flush();
                    dailyAgg?.FlushAll();
                    barChannel?.Writer.Complete();
                    if (writeTask != null)
                        await writeTask;

                    barAgg?.Dispose();
                    dailyAgg?.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Error during LiveDataCollector cleanup");
                }
            }
        }

        _log.Information("LiveDataCollector stopped — ticks={TickCount} bars={BarCount} skipped={TickSkipped}",
            _tickCount, _barCount, _tickSkipped);
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
