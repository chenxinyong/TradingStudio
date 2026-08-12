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
    /// <summary>是否为平仓单。false=开仓(默认), true=平仓。</summary>
    public bool IsCloseOrder { get; init; }
    /// <summary>被平仓位的建仓日期（用于区分平今/平昨，仅SHFE/INE需要）</summary>
    public DateOnly PositionCreatedDate { get; init; }
    /// <summary>平今/平昨标识 — true=平今, false=平昨, null=自动判断（日期比较或非SHFE/INE）。从 CTP PositionDate 直接映射。</summary>
    public bool? IsCloseToday { get; init; }
    /// <summary>退出原因(平仓单专用): SL/TP/Signal/EOD/Manual</summary>
    public string ExitReason { get; init; } = "";
}

/// <summary>
/// 订单方向枚举 — 可根据实际需求扩展，如新增 Short（卖空）等方向。
/// </summary>
public enum OrderDirection { Buy, Sell }

/// <summary>
/// 订单类型枚举 — 可根据实际需求扩展，如新增 StopLimit（止损限价单）、TrailingStop（跟踪止损单）等类型。
/// </summary>
public enum OrderType { Market, Limit, Stop }
/// <summary>
/// 订单状态枚举 — 可根据实际需求扩展，如新增 PendingCancel（撤单中）、Expired（过期）等状态。
/// </summary>
public enum OrderStatus { Submitted, PartiallyFilled, Filled, Cancelled, Rejected }
