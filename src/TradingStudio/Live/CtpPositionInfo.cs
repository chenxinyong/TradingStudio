namespace TradingStudio.Live;

/// <summary>
/// CTP 查询返回的持仓信息（从 ThostFtdcInvestorPositionField 映射）。
/// </summary>
public sealed class CtpPositionInfo
{
    public string InstrumentId { get; init; } = "";
    /// <summary>净持仓手数：正=多头，负=空头</summary>
    public int NetPosition { get; init; }
    /// <summary>开仓均价</summary>
    public double OpenCost { get; init; }
    /// <summary>占用保证金</summary>
    public double UseMargin { get; init; }
}
