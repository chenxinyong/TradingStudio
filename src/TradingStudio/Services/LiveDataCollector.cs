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

    private long _tickCount;
    private long _barCount;
    private readonly ConcurrentDictionary<string, string> _lastTickKey = new(); // 去重：instId → "UpdateTime_Millisec"

    private readonly TickCsvWriter? _tickWriter;

    public LiveDataCollector(CtpLiveFeed feed, IBarStore barStore,
                             HealthMonitor health, Serilog.ILogger log,
                             TickCsvWriter? tickWriter = null)
    {
        _feed = feed;
        _barStore = barStore;
        _health = health;
        _log = log.ForContext<LiveDataCollector>();
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
                var (instId, quote, tradingDay) = item;

                // 去重：同一 UpdateTime+Millisec 的重复快照只写一次
                var tickKey = $"{quote.UpdateTime}_{quote.UpdateMillisec}";
                if (_lastTickKey.TryGetValue(instId, out var prevKey) && prevKey == tickKey)
                    continue;  // 重复快照，跳过
                _lastTickKey[instId] = tickKey;

                Interlocked.Increment(ref _tickCount);

                // 写全量42列 CSV（直接提取 CTP Quote 全部字段）
                // 交易日使用 CTP TradingDay 字段（交易所权威值，正确处理周末/节假日）
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

                // 同时喂 Bar 聚合器
                var record = QuoteConverter.FromCTPQuote(quote);
                barAgg.Feed(record, instId, tradingDay);
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
            dailyAgg.FlushAll();
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
