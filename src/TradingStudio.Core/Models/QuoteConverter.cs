namespace TradingStudio.Core.Models;

/// <summary>CTP Quote 字段 → TickRecord 公共转换方法</summary>
public static class QuoteConverter
{
    /// <summary>从原始字段构建 TickRecord（价格 ×10^7）。Flags 由调用方传入。</summary>
    public static TickRecord FromFields(
        long exchangeTimestamp, long localTimestamp,
        double lastPrice, long volume, double turnover, double openInterest,
        double bid1, int bv1, double ask1, int av1,
        int flags = 0)
    {
        return new TickRecord
        {
            ExchangeTimestamp = exchangeTimestamp,
            LocalTimestamp = localTimestamp,
            LastPrice = (long)(lastPrice * TickRecord.PriceScale),
            Volume = volume,
            Turnover = turnover,
            OpenInterest = openInterest,
            BidPrice1 = (long)(bid1 * TickRecord.PriceScale),
            BidVolume1 = bv1,
            AskPrice1 = (long)(ask1 * TickRecord.PriceScale),
            AskVolume1 = av1,
            Flags = flags,
        };
    }

    /// <summary>CTP Quote → TickRecord。需要 using CTP; 的调用方。</summary>
    public static TickRecord FromCTPQuote(dynamic q)
    {
        return new TickRecord
        {
            ExchangeTimestamp = q.ExchangeTimestamp,
            LocalTimestamp = q.LocalTimestamp,
            LastPrice = (long)(q.LastPrice * TickRecord.PriceScale),
            Volume = q.Volume,
            Turnover = q.Turnover,
            OpenInterest = q.OpenInterest,
            BidPrice1 = (long)(q.BidPrice1 * TickRecord.PriceScale),
            BidVolume1 = q.BidVolume1,
            AskPrice1 = (long)(q.AskPrice1 * TickRecord.PriceScale),
            AskVolume1 = q.AskVolume1,
            Flags = (q.LastPrice >= q.UpperLimitPrice && q.UpperLimitPrice > 0 ? 1 : 0)
                  | (q.LastPrice <= q.LowerLimitPrice && q.LowerLimitPrice > 0 ? 2 : 0),
        };
    }

    /// <summary>CTP 交易日解析（yyyyMMdd → DateOnly，夜盘归属正确）</summary>
    public static DateOnly ParseTradingDay(string? tradingDay)
        => DateOnly.TryParseExact(tradingDay, "yyyyMMdd", out var d)
            ? d : DateOnly.FromDateTime(DateTime.Today);
}
