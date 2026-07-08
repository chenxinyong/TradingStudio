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

    public BarAggregator Agg1Min { get; }
    public DailyBarAggregator AggDay { get; }

    public long QuoteCount;
    public long BarCount;
    public long TickSkipped;

    /// <summary>仅 Top30 品种写 CSV（空集合 = 全量写）</summary>
    public QuotePipeline(TickCsvWriter tickWriter, HashSet<string> top30Codes)
    {
        _tickWriter = tickWriter;
        _top30Codes = top30Codes;
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

        // Bar 聚合
        Agg1Min.Feed(record, instId, tradingDay);
        AggDay.Feed(record, instId, tradingDay);
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

    /// <summary>收盘/会话结束时 flush 未完成的 Bar</summary>
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
