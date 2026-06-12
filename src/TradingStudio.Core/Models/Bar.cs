using System.Runtime.InteropServices;

namespace TradingStudio.Core.Models;

/// <summary>
/// 1分钟 K 线 (Bar)。从 TickRecord 流聚合而来。
/// 所有价格字段 × 10^7 存为 long，与 TickRecord 一致。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Bar
{
    public string InstrumentId { get; init; }  // "ag2608"
    public DateOnly TradingDay { get; init; }   // CTP 交易日（夜盘归属前一天）
    public DateTime BarTime { get; init; }      // Bar 起始时间，精确到分钟

    public long Open { get; set; }              // 开盘价 × 10^7
    public long High { get; set; }              // 最高价
    public long Low { get; set; }               // 最低价
    public long Close { get; set; }             // 收盘价
    public long Volume { get; set; }            // 成交量（delta）
    public double Turnover { get; set; }        // 成交额（delta）
    public double OpenInterest { get; set; }    // 持仓量（快照）
    public int TickCount { get; set; }          // 构成此 Bar 的 tick 数

    // === 计算属性 ===
    public double OpenDouble => (double)Open / TickRecord.PriceScale;
    public double HighDouble => (double)High / TickRecord.PriceScale;
    public double LowDouble => (double)Low / TickRecord.PriceScale;
    public double CloseDouble => (double)Close / TickRecord.PriceScale;
    public bool IsUp => Close >= Open;

    public override string ToString() =>
        $"[{BarTime:HH:mm}] {InstrumentId} O={OpenDouble:F2} H={HighDouble:F2} L={LowDouble:F2} C={CloseDouble:F2} V={Volume}";
}
