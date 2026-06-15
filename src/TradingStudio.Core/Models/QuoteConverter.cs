namespace TradingStudio.Core.Models;

/// <summary>CTP Quote → TickRecord 公共转换方法（消除 3 处重复代码）</summary>
public static class QuoteConverter
{
    /// <summary>CTP C# 回调 Quote → TickRecord。价格 ×10^7。</summary>
    public static TickRecord FromQuote(CTP.Quote q)
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

    /// <summary>CTP Quote 的交易日解析（yyyyMMdd，夜盘归属正确）</summary>
    public static DateOnly ParseTradingDay(string? tradingDay)
        => DateOnly.TryParseExact(tradingDay, "yyyyMMdd", out var d)
            ? d : DateOnly.FromDateTime(DateTime.Today);
}
