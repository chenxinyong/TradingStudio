using System.Globalization;

namespace TradingStudio.Core.Models;

/// <summary>
/// Tick CSV 行数据 — 金数源 44 列格式。
/// 替代 TickCsvWriter.Write() 的 17 参数上帝方法，字段顺序与 CSV 列顺序一致。
/// </summary>
public readonly record struct CsvTickRecord
{
    // ── 元数据 ──
    public string InstrumentId { get; init; }
    public string ExchangeId { get; init; }
    public string TradingDay { get; init; }

    // ── 时间 ──
    public string UpdateTime { get; init; }   // HH:mm:ss
    public int UpdateMillisec { get; init; }  // 0-999

    // ── 价格 (×10⁷ 存为 long 更好，但 CTP 原始就是 double) ──
    public double LastPrice { get; init; }
    public double PreSettlementPrice { get; init; }
    public double PreClosePrice { get; init; }
    public double PreOpenInterest { get; init; }
    public double OpenPrice { get; init; }
    public double HighestPrice { get; init; }
    public double LowestPrice { get; init; }
    public double ClosePrice { get; init; }
    public double SettlementPrice { get; init; }
    public double UpperLimitPrice { get; init; }
    public double LowerLimitPrice { get; init; }
    public double AveragePrice { get; init; }

    // ── 量 ──
    public int Volume { get; init; }
    public double Turnover { get; init; }
    public double OpenInterest { get; init; }

    // ── 五档盘口 ──
    public double BidPrice1 { get; init; }
    public int BidVolume1 { get; init; }
    public double AskPrice1 { get; init; }
    public int AskVolume1 { get; init; }

    public double BidPrice2 { get; init; }
    public int BidVolume2 { get; init; }
    public double AskPrice2 { get; init; }
    public int AskVolume2 { get; init; }

    public double BidPrice3 { get; init; }
    public int BidVolume3 { get; init; }
    public double AskPrice3 { get; init; }
    public int AskVolume3 { get; init; }

    public double BidPrice4 { get; init; }
    public int BidVolume4 { get; init; }
    public double AskPrice4 { get; init; }
    public int AskVolume4 { get; init; }

    public double BidPrice5 { get; init; }
    public int BidVolume5 { get; init; }
    public double AskPrice5 { get; init; }
    public int AskVolume5 { get; init; }

    // ── 工厂方法 ──

    /// <summary>从 CTP Quote 全字段创建（Live/Collect 路径）</summary>
    public static CsvTickRecord FromCtpQuote(dynamic q, string instrumentId, string exchangeId, string tradingDay)
    {
        return new CsvTickRecord
        {
            InstrumentId = instrumentId,
            ExchangeId = exchangeId,
            TradingDay = tradingDay,
            UpdateTime = q.UpdateTime ?? "",
            UpdateMillisec = q.UpdateMillisec,
            LastPrice = q.LastPrice,
            PreSettlementPrice = q.PreSettlementPrice,
            PreClosePrice = q.PreClosePrice,
            PreOpenInterest = q.PreOpenInterest,
            OpenPrice = q.OpenPrice,
            HighestPrice = q.HighestPrice,
            LowestPrice = q.LowestPrice,
            ClosePrice = q.ClosePrice,
            SettlementPrice = q.SettlementPrice,
            UpperLimitPrice = q.UpperLimitPrice,
            LowerLimitPrice = q.LowerLimitPrice,
            AveragePrice = q.AveragePrice,
            Volume = q.Volume,
            Turnover = q.Turnover,
            OpenInterest = q.OpenInterest,
            BidPrice1 = q.BidPrice1, BidVolume1 = q.BidVolume1,
            AskPrice1 = q.AskPrice1, AskVolume1 = q.AskVolume1,
            BidPrice2 = q.BidPrice2, BidVolume2 = q.BidVolume2,
            AskPrice2 = q.AskPrice2, AskVolume2 = q.AskVolume2,
            BidPrice3 = q.BidPrice3, BidVolume3 = q.BidVolume3,
            AskPrice3 = q.AskPrice3, AskVolume3 = q.AskVolume3,
            BidPrice4 = q.BidPrice4, BidVolume4 = q.BidVolume4,
            AskPrice4 = q.AskPrice4, AskVolume4 = q.AskVolume4,
            BidPrice5 = q.BidPrice5, BidVolume5 = q.BidVolume5,
            AskPrice5 = q.AskPrice5, AskVolume5 = q.AskVolume5,
        };
    }

    /// <summary>从 TickRecord 创建（仅 L1 深度，回测/精简路径）</summary>
    public static CsvTickRecord FromTickRecord(TickRecord tick, string instrumentId, string exchangeId, DateOnly tradingDay)
    {
        var price = tick.LastPrice / (double)TickRecord.PriceScale;
        var bp1 = tick.BidPrice1 / (double)TickRecord.PriceScale;
        var ap1 = tick.AskPrice1 / (double)TickRecord.PriceScale;
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(tick.ExchangeTimestamp);

        return new CsvTickRecord
        {
            InstrumentId = instrumentId,
            ExchangeId = exchangeId,
            TradingDay = tradingDay.ToString("yyyyMMdd"),
            UpdateTime = dt.ToString("HH:mm:ss"),
            UpdateMillisec = dt.Millisecond,
            LastPrice = price,
            OpenPrice = price,
            HighestPrice = price,
            LowestPrice = price,
            Volume = (int)tick.Volume,
            Turnover = tick.Turnover,
            OpenInterest = tick.OpenInterest,
            BidPrice1 = bp1, BidVolume1 = tick.BidVolume1,
            AskPrice1 = ap1, AskVolume1 = tick.AskVolume1,
        };
    }
}
