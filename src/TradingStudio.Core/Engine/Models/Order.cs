namespace TradingStudio.Core.Engine;

/// <summary>订单实体</summary>
public class Order
{
    public long OrderId { get; init; }
    public string InstrumentId { get; init; } = "";
    public string StrategyId { get; init; } = "";
    public OrderDirection Direction { get; init; }
    public OrderType Type { get; init; }
    public int Quantity { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public string? Tag { get; init; }
    public DateTimeOffset CreatedTime { get; init; }
    public OrderStatus Status { get; set; } = OrderStatus.Submitted;
    public int FilledQuantity { get; set; }
    public decimal AvgFillPrice { get; set; }
}

public enum OrderDirection { Buy, Sell }
public enum OrderType { Market, Limit, Stop }
public enum OrderStatus { Submitted, PartiallyFilled, Filled, Cancelled, Rejected }
