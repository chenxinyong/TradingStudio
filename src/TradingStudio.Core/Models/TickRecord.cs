using System.Runtime.InteropServices;

namespace TradingStudio.Core.Models;

/// Tick record — 80 bytes fixed size for .tick file storage.
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 80)]
public readonly record struct TickRecord
{
    public long ExchangeTimestamp { get; init; }
    public long LocalTimestamp { get; init; }
    public long LastPrice { get; init; }
    public long BidPrice1 { get; init; }
    public long AskPrice1 { get; init; }
    public long Volume { get; init; }
    public double Turnover { get; init; }
    public double OpenInterest { get; init; }
    public int BidVolume1 { get; init; }
    public int AskVolume1 { get; init; }
    public int Flags { get; init; }

    public const long PriceScale = 10_000_000;
    public const int RecordSize = 80;

    // Flag bits
    public const int FlagUpperLimit  = 1 << 0;
    public const int FlagLowerLimit  = 1 << 1;
    public const int FlagAuction     = 1 << 2;
    public const int FlagOpenInstant = 1 << 3;

    public bool IsUpperLimit => (Flags & FlagUpperLimit) != 0;
    public bool IsLowerLimit => (Flags & FlagLowerLimit) != 0;
    public bool IsAuction    => (Flags & FlagAuction) != 0;

    // === 计算属性 (从 80B 原始字段派生，零额外存储) ===

    /// <summary>最新价 (double)</summary>
    public double LastPriceDouble => (double)LastPrice / PriceScale;

    /// <summary>买一价 (double)</summary>
    public double BidPrice1Double => (double)BidPrice1 / PriceScale;

    /// <summary>卖一价 (double)</summary>
    public double AskPrice1Double => (double)AskPrice1 / PriceScale;

    /// <summary>买卖价差 (tick 数)</summary>
    public long Spread => AskPrice1 - BidPrice1;

    /// <summary>买卖价差 (double)</summary>
    public double SpreadDouble => (AskPrice1 - BidPrice1) / (double)PriceScale;

    /// <summary>交易所时间 (UTC)</summary>
    public DateTimeOffset ExchangeTime =>
        DateTimeOffset.FromUnixTimeMilliseconds(ExchangeTimestamp);

    /// <summary>本地接收时间 (UTC)</summary>
    public DateTimeOffset LocalTime =>
        DateTimeOffset.FromUnixTimeMilliseconds(LocalTimestamp);

    /// <summary>网络+处理延迟 (毫秒)</summary>
    public double LatencyMs => (LocalTimestamp - ExchangeTimestamp);

    public override string ToString() =>
        $"Tick(Price={LastPriceDouble:F2}, Vol={Volume}, OI={OpenInterest:F0}, Spread={SpreadDouble:F4})";
}
