namespace TradingStudio.Core.Engine;

/// <summary>订单生命周期事件</summary>
public class OrderEvent
{
    public long OrderId { get; init; }
    public string InstrumentId { get; init; } = "";
    public string StrategyId { get; init; } = "";
    public OrderDirection Direction { get; init; }
    public int Quantity { get; init; }
    public int OrderQty { get; init; }
    public int FilledQty { get; set; }
    public OrderEventType Type { get; init; }
    public decimal FillPrice { get; set; }
    public decimal Fee { get; set; }
    public decimal Slippage { get; set; }
    public string? Message { get; init; }
    public DateTimeOffset Time { get; init; }
}

public enum OrderEventType { Submitted, Filled, PartiallyFilled, Cancelled, Rejected }
