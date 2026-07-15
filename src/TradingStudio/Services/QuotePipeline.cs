using TradingStudio.Core.Models;
using TradingStudio.Data.Aggregation;
using TradingStudio.Data.Storage;

namespace TradingStudio.Services;

/// <summary>
/// Quote 处理管线 — CTP Quote → TickRecord → Top30 过滤 → Tick CSV → Bar 聚合。
/// CollectService 和 LiveDataCollector 共享此逻辑，消除 ~40% 代码重复。
/// </summary>
public class QuotePipeline : IDisposable
{
    private readonly HashSet<string> _top30Codes;
    private readonly TickCsvWriter _tickWriter;
    private readonly TickAggregationGate? _gate;

    public BarAggregator Agg1Min { get; }
    public DailyBarAggregator AggDay { get; }

    public long QuoteCount;
    public long BarCount;
    public long TickSkipped;
    public long AggFiltered;

    /// <summary>仅 Top30 品种写 CSV（空集合 = 全量写）。registry 非空时启用聚合闸门（时段+陈旧快照过滤），null = 关闭过滤。</summary>
    public QuotePipeline(TickCsvWriter tickWriter, HashSet<string> top30Codes, FutureRegistry? registry = null)
    {
        _tickWriter = tickWriter;
        _top30Codes = top30Codes;
        _gate = registry != null ? new TickAggregationGate(registry) : null;
        Agg1Min = new BarAggregator();
        AggDay = new DailyBarAggregator();
    }

    /// <summary>处理一条 CTP Quote。线程安全。</summary>
    public void Feed(CTP.Quote q)
    {
        if (string.IsNullOrEmpty(q.InstrumentID)) return;

        var instId = ContractCodeGenerator.Normalize(q.InstrumentID).ToLowerInvariant(); // CZCE大写→小写, 与其他交易所统一
        var record = QuoteConverter.FromCTPQuote(q);
        var tradingDay = QuoteConverter.ParseTradingDay(q.TradingDay);

        // Bar 聚合（闸门只挡聚合路径：盘外/陈旧快照 tick 不进 Bar，CSV 照写）
        // ExchangeTime 是"北京墙钟当UTC"编码 → TimeOfDay 即北京时间；LocalTime 是真实 UTC → +8h
        var pass = _gate == null || _gate.ShouldAggregate(instId,
            record.ExchangeTime.TimeOfDay, record.LocalTime.AddHours(8).TimeOfDay);
        if (pass)
        {
            Agg1Min.Feed(record, instId, tradingDay);
            AggDay.Feed(record, instId, tradingDay);
        }
        else
        {
            Interlocked.Increment(ref AggFiltered);
        }
        Interlocked.Increment(ref QuoteCount);

        // Top 30 分层：空集合 = 全量写 CSV
        var productCode = instId.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (_top30Codes.Count > 0 && !_top30Codes.Contains(productCode))
        {
            Interlocked.Increment(ref TickSkipped);
            return;
        }

        var row = CsvTickRecord.FromCtpQuote(q, instId,
            string.IsNullOrEmpty(q.ExchangeID) ? TickCsvWriter.GuessExchange(instId) : q.ExchangeID,
            q.TradingDay);
        _tickWriter.Write(in row);
    }

    /// <summary>
    /// 重连前/收盘时的轻量 flush：1min 正常发射（其累计量状态本就跨 flush 保留），
    /// 日线只发快照不清状态——交易日未结束（夜盘→次日日盘），清了会丢夜盘 OHLCV。
    /// </summary>
    public void FlushSnapshots()
    {
        Agg1Min.Flush();
        AggDay.EmitSnapshots();
    }

    /// <summary>停机 flush：发射全部并清空状态。仅 Dispose 路径使用。</summary>
    public void Flush()
    {
        Agg1Min.Flush();
        AggDay.FlushAll();
    }

    public void Dispose()
    {
        Agg1Min.Dispose();
        AggDay.Dispose();
    }
}
