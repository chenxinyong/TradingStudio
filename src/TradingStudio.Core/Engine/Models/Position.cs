namespace TradingStudio.Core.Engine;

/// <summary>持仓实体</summary>
public class Position
{
    public string InstrumentId { get; init; } = "";
    /// <summary>今仓（带符号净额，+ 多 - 空）。新开仓与当日加仓入此；结算后滚入 QuantityYesterday。</summary>
    public int QuantityToday { get; set; }
    /// <summary>昨仓（带符号净额，+ 多 - 空）。由 SettleDaily 从今仓滚入。</summary>
    public int QuantityYesterday { get; set; }
    /// <summary>净持仓手数：今仓 + 昨仓（+ 多, - 空）。只读，由两个分桶派生。</summary>
    public int Quantity => QuantityToday + QuantityYesterday;
    public decimal AvgPrice { get; set; }
    public double MarketPrice { get; set; }
    public double UnrealizedPnl { get; set; }
    public decimal Margin { get; set; }
    public decimal Commission { get; set; }
    public DateTimeOffset CreatedTime { get; init; }
    public string StrategyId { get; set; } = "";
    /// <summary>CTP PositionDate — '1'=今仓, '2'=昨仓, '\0'=未设置（回测模式）。平今/平昨判断的直接依据。</summary>
    public char PositionDate { get; set; } = '\0';
}

/// <summary>
/// 持仓对账状态。实盘启动/重连后，CTP 持仓查询结果与本地账本逐项比对：
/// NotReconciled=尚未对账（回测/启动前）；Ok=本轮对账一致；Mismatch=本地账本与 CTP 不一致（阻断新开仓）。
/// </summary>
public enum ReconcileStatus { NotReconciled, Ok, Mismatch }
