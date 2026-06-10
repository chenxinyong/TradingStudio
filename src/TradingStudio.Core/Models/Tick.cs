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
    public const int FLAG_UPPER_LIMIT = 1 << 0;
    public const int FLAG_LOWER_LIMIT = 1 << 1;
    public const int FLAG_AUCTION     = 1 << 2;
    public const int FLAG_OPEN_INSTANT = 1 << 3;

    public bool IsUpperLimit => (Flags & FLAG_UPPER_LIMIT) != 0;
    public bool IsLowerLimit => (Flags & FLAG_LOWER_LIMIT) != 0;
}
