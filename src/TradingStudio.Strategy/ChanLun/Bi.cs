namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 笔 — spec §3.1。
/// 连接两个异类分型，满足最小K线数和方向约束。
/// </summary>
public record Bi(
    Direction Type,
    Fractal StartFx,
    Fractal EndFx,
    int StartIdx,
    int EndIdx,
    int BarCount,       // 包含的标准K线数
    double Low,         // 笔内最低价
    double High,        // 笔内最高价
    DateTime DtStart,
    DateTime DtEnd
)
{
    /// <summary>笔的力度（涨跌幅度，万分比） — spec §3.3</summary>
    public double Power => Type == Direction.Up
        ? (High - Low) / Low * 10000
        : (High - Low) / High * 10000;

    /// <summary>涨跌幅（百分比）</summary>
    public double ChangePct => Type == Direction.Up
        ? (EndFx.Price - StartFx.Price) / StartFx.Price * 100
        : (StartFx.Price - EndFx.Price) / StartFx.Price * 100;
}
