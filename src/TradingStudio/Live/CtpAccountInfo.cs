namespace TradingStudio.Live;

/// <summary>
/// CTP 查询返回的资金账户信息（从 ThostFtdcTradingAccountField 映射）。
/// </summary>
public sealed class CtpAccountInfo
{
    /// <summary>动态权益（含浮动盈亏 + 占用保证金），重启后恢复权益的权威来源。</summary>
    public double Balance { get; init; }
    /// <summary>昨结算权益，用作当日盈亏基准（TodayPnL = Balance - PreBalance）。</summary>
    public double PreBalance { get; init; }
    /// <summary>持仓盈亏（今日浮动盈亏）。恢复权益时需扣除，避免与 Bar 驱动的浮盈双重计算。</summary>
    public double PositionProfit { get; init; }
    /// <summary>平仓盈亏（今日已实现盈亏），仅用于日志/审计。</summary>
    public double CloseProfit { get; init; }
    /// <summary>可用资金</summary>
    public double Available { get; init; }
    /// <summary>当前占用保证金</summary>
    public double CurrMargin { get; init; }
}
