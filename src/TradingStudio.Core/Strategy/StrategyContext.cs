using TradingStudio.Core.Engine;
using TradingStudio.Core.Indicators;
using TradingStudio.Core.Models;

namespace TradingStudio.Core.Strategy;

/// <summary>
/// 策略上下文 — 策略与引擎之间的唯一触点。
/// 策略不知道引擎内部结构，只能通过 Context 查询行情、下单、查仓位。
/// </summary>
public class StrategyContext
{
    // ═══ 标识 ═══
    public string StrategyId { get; }

    public StrategyContext(string strategyId) => StrategyId = strategyId;

    // ═══ 行情数据 ═══
    public virtual IReadOnlyList<Bar> GetBarHistory(string instrumentId) =>
        throw new NotImplementedException();
    public virtual IReadOnlyList<Bar> GetRecentBars(string instrumentId, int count) =>
        throw new NotImplementedException();
    public virtual IReadOnlyList<TickRecord> GetTickHistory(string instrumentId, int maxCount = 1000) =>
        throw new NotImplementedException();
    public virtual DateTimeOffset CurrentTime => DateTimeOffset.UtcNow;

    // ═══ 指标查询 ═══
    public virtual T RegisterIndicator<T>(string instrumentId, T indicator, string tag = "")
        where T : IIndicator =>
        throw new NotImplementedException();
    public virtual double GetIndicatorValue(string instrumentId, string indicatorName, string tag = "") =>
        double.NaN;
    public virtual T? GetIndicator<T>(string instrumentId, string tag = "") where T : class, IIndicator => null;

    // ═══ 交易 ═══
    public virtual OrderTicket MarketBuy(string instrumentId, int quantity, string? tag = null) =>
        throw new NotImplementedException();
    public virtual OrderTicket MarketSell(string instrumentId, int quantity, string? tag = null) =>
        throw new NotImplementedException();
    public virtual OrderTicket ClosePosition(string instrumentId) =>
        throw new NotImplementedException();
    public virtual OrderTicket LimitBuy(string instrumentId, int quantity, decimal limitPrice) =>
        throw new NotImplementedException();
    public virtual OrderTicket LimitSell(string instrumentId, int quantity, decimal limitPrice) =>
        throw new NotImplementedException();
    public virtual OrderTicket StopBuy(string instrumentId, int quantity, decimal stopPrice) =>
        throw new NotImplementedException();
    public virtual OrderTicket StopSell(string instrumentId, int quantity, decimal stopPrice) =>
        throw new NotImplementedException();
    public virtual bool CancelOrder(long orderId) => false;

    // ═══ 仓位与资金（只看到自己的分账） ═══
    public virtual Position? GetPosition(string instrumentId) => null;
    public virtual IReadOnlyList<Position> Positions => [];
    public virtual decimal Equity => 0;
    public virtual decimal AvailableCash => 0;
    public virtual decimal AllocatedCapital => 0;

    // ═══ 品种信息 ═══
    public virtual Future GetFuture(string instrumentId) =>
        throw new NotImplementedException();
    public virtual IReadOnlyList<string> SubscribedInstruments => [];

    // ═══ 风控收紧（运行时，只能收紧不能放宽） ═══
    public virtual bool TightenRisk(string ruleName, object newValue) => false;

    // ═══ 日志 ═══
    public virtual void Log(string message) { }
    public virtual void LogWarning(string message) { }
    public virtual void LogError(string message) { }
}
