namespace TradingStudio.Core.Engine;

/// <summary>平仓交易记录</summary>
public class Trade
{
    public string InstrumentId { get; init; } = "";
    public int Quantity { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal PnL { get; init; }
    public decimal Fee { get; init; }
    public decimal Slippage { get; init; }
    public DateTime EntryTime { get; init; }
    public DateTime ExitTime { get; init; }
    public bool IsWin => PnL > 0;
    public string StrategyId { get; init; } = "";
}
