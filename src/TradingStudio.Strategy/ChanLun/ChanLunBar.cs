namespace TradingStudio.Strategy.ChanLun;

/// <summary>
/// 缠论K线 — 轻量级数据结构，独立于 Core.Models.Bar。
/// 可通过扩展方法从 Bar 转换。
/// </summary>
public class ChanLunBar
{
    public DateTime Dt { get; init; }
    public double Open { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public double Close { get; init; }
    public long Volume { get; init; }

    public override string ToString() =>
        $"[{Dt:yyyy-MM-dd HH:mm}] O={Open:F2} H={High:F2} L={Low:F2} C={Close:F2} V={Volume}";
}
