namespace TradingStudio.Core.Engine;

/// <summary>订单生命周期事件</summary>
public class OrderEvent
{
    public long OrderId { get; init; }
    public string InstrumentId { get; init; } = "";
    public string StrategyId { get; init; } = "";
    /// <summary>审计链 TraceId：贯穿 Submitted/Accepted/Rejected/Filled 全事件，与 Order.TraceId 一致。</summary>
    public string TraceId { get; init; } = "";
    public OrderDirection Direction { get; init; }
    public int Quantity { get; init; }
    public int OrderQty { get; init; }
    public int FilledQty { get; set; }
    public OrderEventType Type { get; init; }
    public decimal FillPrice { get; set; }
    public decimal Fee { get; set; }
    public decimal Slippage { get; set; }
    /// <summary>CTP TradeID（成交编号，幂等键）。回测撮合不填（空）。</summary>
    public string TradeId { get; init; } = "";
    /// <summary>CTP OrderSysID（报单编号，撤单定位）。</summary>
    public string OrderSysId { get; init; } = "";
    /// <summary>CTP ExchangeID（交易所代码）。</summary>
    public string ExchangeId { get; init; } = "";
    /// <summary>CTP TradeDate（成交日期 yyyyMMdd）。</summary>
    public string TradeDate { get; init; } = "";
    /// <summary>CTP OffsetFlag（Open/Close/CloseToday/CloseYesterday/...）。回测为空。</summary>
    public string OffsetFlag { get; init; } = "";
    public string? Message { get; init; }
    public string ExitReason { get; init; } = "";
    public DateTimeOffset Time { get; init; }
}

public enum OrderEventType { Submitted, Accepted, Filled, PartiallyFilled, Cancelled, Rejected, Unknown }
