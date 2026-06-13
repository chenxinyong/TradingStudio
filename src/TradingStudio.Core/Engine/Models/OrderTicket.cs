namespace TradingStudio.Core.Engine;

/// <summary>订单回执 — 策略手里的存根</summary>
public class OrderTicket
{
    public long OrderId { get; init; }
    public OrderStatus Status { get; set; } = OrderStatus.Submitted;
    public int FilledQuantity { get; set; }
    public decimal AvgFillPrice { get; set; }
    public bool IsCompleted => Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected;
}
