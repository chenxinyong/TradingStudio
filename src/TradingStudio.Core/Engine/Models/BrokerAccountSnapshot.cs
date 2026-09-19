namespace TradingStudio.Core.Engine;

/// <summary>
/// 券商（CTP）资金账户快照 —— 对账（ReconcileEquity）的权威输入。
/// 从 CTP ThostFtdcTradingAccountField 映射，字段语义见 CtpAccountInfo。
/// </summary>
public record BrokerAccountSnapshot
{
    /// <summary>动态权益（含浮动盈亏 + 占用保证金），权威总权益。</summary>
    public decimal Balance { get; init; }
    /// <summary>昨结算权益，当日盈亏基准（TodayPnL = Balance - PreBalance）。</summary>
    public decimal PreBalance { get; init; }
    /// <summary>持仓盈亏（今日浮动盈亏）。恢复权益时扣除，避免与 Bar 驱动浮盈双重计算。</summary>
    public decimal PositionProfit { get; init; }
    /// <summary>平仓盈亏（今日已实现盈亏），仅审计。</summary>
    public decimal CloseProfit { get; init; }
    /// <summary>可用资金。</summary>
    public decimal Available { get; init; }
    /// <summary>当前占用保证金（CTP 权威，对账时刻采信，覆盖本地各持仓 margin 之和）。</summary>
    public decimal CurrMargin { get; init; }
}
