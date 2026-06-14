using TradingStudio.Core.Engine;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine;

/// <summary>
/// 风控控制器 — 管理风控规则链，三级检查（Pre-Order / Post-Fill / Periodic）。
/// Phase 2a: 最小实现，未来扩展规则注册。
/// </summary>
public class RiskController
{
    private readonly List<IRiskRule> _rules = new();

    public void AddRule(IRiskRule rule) => _rules.Add(rule);

    /// <summary>下单前检查。任一规则 Reject → 拒绝整个订单。</summary>
    public RiskCheckResult CheckPreOrder(Order order, IPortfolioState portfolio)
    {
        foreach (var rule in _rules)
        {
            var result = rule.CheckPreOrder(order, portfolio);
            if (!result.Passed)
                return result;
        }
        return RiskCheckResult.Pass;
    }

    /// <summary>成交后检查。返回所有 Warning。</summary>
    public IReadOnlyList<RiskCheckResult> CheckPostFill(OrderEvent fill, IPortfolioState portfolio)
    {
        var results = new List<RiskCheckResult>();
        foreach (var rule in _rules)
        {
            var result = rule.CheckPostFill(fill, portfolio);
            if (result.Level == RiskCheckLevel.Warning)
                results.Add(result);
        }
        return results;
    }

    /// <summary>定期检查（每个时间步）。</summary>
    public IReadOnlyList<RiskCheckResult> CheckPeriodic(IPortfolioState portfolio)
    {
        var results = new List<RiskCheckResult>();
        foreach (var rule in _rules)
        {
            var result = rule.CheckPeriodic(portfolio);
            if (result.Level != RiskCheckLevel.Pass)
                results.Add(result);
        }
        return results;
    }
}
