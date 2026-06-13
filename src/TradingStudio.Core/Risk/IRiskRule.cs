using TradingStudio.Core.Engine;

namespace TradingStudio.Core.Risk;

/// <summary>
/// 单条风控规则。
/// 实现类必须是无状态的（或只依赖配置），以便回测中确定性执行。
/// </summary>
public interface IRiskRule
{
    /// <summary>规则名称（用于日志和报告）</summary>
    string Name { get; }

    /// <summary>下单前检查。阻断型 —— Reject 则订单不进入撮合。</summary>
    RiskCheckResult CheckPreOrder(Order order, IPortfolioState portfolio);

    /// <summary>成交后检查。非阻断型 —— Warning 通知策略但不中断交易。</summary>
    RiskCheckResult CheckPostFill(OrderEvent fill, IPortfolioState portfolio);

    /// <summary>定期检查（每个时间步）。用于回撤监控、总敞口监控等。</summary>
    RiskCheckResult CheckPeriodic(IPortfolioState portfolio);
}
