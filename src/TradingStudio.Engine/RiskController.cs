using TradingStudio.Core.Engine;
using TradingStudio.Core.Risk;

namespace TradingStudio.Engine;

/// <summary>
/// 风控控制器 — 管理风控规则链，三级检查（Pre-Order / Post-Fill / Periodic）。
/// 阈值通过构造函数传入（Program.cs 从 appsettings.json Risk 段读取）。
/// </summary>
public class RiskController
{
    private readonly List<IRiskRule> _rules = new();

    public RiskController(int maxPosition = 5, int maxOrderQty = 100, decimal maxDrawdown = 0.25m)
    {
        _rules.Add(new MaxPositionPerInstrumentRule(maxPosition));
        _rules.Add(new MaxOrderQuantityRule(maxOrderQty));
        _rules.Add(new MaxDrawdownRule(maxDrawdown));
    }

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

    // ═══════════════════════════════════════════
    // 内置规则
    // ═══════════════════════════════════════════

    /// <summary>单品种最大持仓手数限制</summary>
    private class MaxPositionPerInstrumentRule : IRiskRule
    {
        private readonly int _maxPositions;
        public string Name => "MaxPositionPerInstrument";

        public MaxPositionPerInstrumentRule(int maxPositions) => _maxPositions = maxPositions;

        public RiskCheckResult CheckPreOrder(Order order, IPortfolioState portfolio)
        {
            var existing = portfolio.GetPosition(order.InstrumentId);
            var newQty = (existing?.Quantity ?? 0) + order.Quantity;
            if (Math.Abs(newQty) > _maxPositions)
                return RiskCheckResult.Reject(Name,
                    $"Position limit: {Math.Abs(newQty)} > {_maxPositions} for {order.InstrumentId}");
            return RiskCheckResult.Pass;
        }

        public RiskCheckResult CheckPostFill(OrderEvent fill, IPortfolioState portfolio) => RiskCheckResult.Pass;
        public RiskCheckResult CheckPeriodic(IPortfolioState portfolio) => RiskCheckResult.Pass;
    }

    /// <summary>单笔最大下单手数限制</summary>
    private class MaxOrderQuantityRule : IRiskRule
    {
        private readonly int _maxQty;
        public string Name => "MaxOrderQuantity";

        public MaxOrderQuantityRule(int maxQty) => _maxQty = maxQty;

        public RiskCheckResult CheckPreOrder(Order order, IPortfolioState portfolio)
        {
            if (order.Quantity > _maxQty)
                return RiskCheckResult.Reject(Name,
                    $"Order quantity {order.Quantity} > max {_maxQty}");
            return RiskCheckResult.Pass;
        }

        public RiskCheckResult CheckPostFill(OrderEvent fill, IPortfolioState portfolio) => RiskCheckResult.Pass;
        public RiskCheckResult CheckPeriodic(IPortfolioState portfolio) => RiskCheckResult.Pass;
    }

    /// <summary>最大回撤风控：Drawdown > 阈值 → 拒绝新开仓</summary>
    private class MaxDrawdownRule : IRiskRule
    {
        private readonly decimal _threshold;
        public string Name => "MaxDrawdown";

        public MaxDrawdownRule(decimal threshold) => _threshold = threshold;

        public RiskCheckResult CheckPreOrder(Order order, IPortfolioState portfolio)
        {
            if (portfolio.StartingCapital > 0)
            {
                var drawdown = 1m - portfolio.Equity / portfolio.StartingCapital;
                if (drawdown > _threshold)
                    return RiskCheckResult.Reject(Name,
                        $"Drawdown {drawdown:P2} > {_threshold:P2} — rejecting new orders");
            }
            return RiskCheckResult.Pass;
        }

        public RiskCheckResult CheckPostFill(OrderEvent fill, IPortfolioState portfolio) => RiskCheckResult.Pass;
        public RiskCheckResult CheckPeriodic(IPortfolioState portfolio) => RiskCheckResult.Pass;
    }
}
