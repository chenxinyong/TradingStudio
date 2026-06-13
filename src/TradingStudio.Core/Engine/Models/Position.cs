namespace TradingStudio.Core.Engine;

/// <summary>持仓实体</summary>
public class Position
{
    public string InstrumentId { get; init; } = "";
    public int Quantity { get; set; }           // + 多, - 空
    public decimal AvgPrice { get; set; }
    public double MarketPrice { get; set; }
    public double UnrealizedPnl { get; set; }
    public decimal Margin { get; set; }
    public decimal Commission { get; set; }
    public DateTimeOffset CreatedTime { get; init; }
    public string StrategyId { get; set; } = "";
}
