using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;

namespace TradingStudio.Engine;

/// <summary>
/// 仓位与资金管理 — 总账 + 各策略分账。
/// 引擎内部持有，策略通过 StrategyContext 访问自己的分账。
/// </summary>
public class PortfolioManager
{
    private readonly Dictionary<string, Position> _allPositions = new();
    private readonly SortedDictionary<string, SubPortfolio> _subPortfolios = new();

    // ═══ 总账 ═══
    public decimal TotalStartingCapital { get; }
    public decimal TotalCash { get; private set; }
    public decimal TotalMarginUsed { get; private set; }
    public decimal TotalEquity => TotalCash + _allPositions.Values.Sum(p => (decimal)p.UnrealizedPnl);

    public PortfolioManager(decimal totalCapital)
    {
        TotalStartingCapital = totalCapital;
        TotalCash = totalCapital;
    }

    // ═══ 策略分账 ═══
    public SubPortfolio GetSubPortfolio(string strategyId) =>
        _subPortfolios.TryGetValue(strategyId, out var sp) ? sp :
        throw new InvalidOperationException($"未找到策略分账: {strategyId}");

    public IReadOnlyList<SubPortfolio> SubPortfolios => _subPortfolios.Values.ToList();

    public void CreateSubPortfolio(string strategyId, decimal allocatedCapital)
    {
        _subPortfolios[strategyId] = new SubPortfolio(strategyId, allocatedCapital);
    }

    // ═══ 操作（Phase 2a 实现） ═══
    public void UpdateMarketPrice(string instrumentId, TickRecord tick)
    {
        // Phase 2b 实现
    }

    public Trade? ProcessFill(OrderEvent fill, FutureRegistry registry)
    {
        // Phase 2a 实现
        throw new NotImplementedException("Phase 2a: 实现成交处理");
    }
}

/// <summary>策略分账</summary>
public class SubPortfolio
{
    public string StrategyId { get; }
    public decimal AllocatedCapital { get; }
    public decimal Cash { get; internal set; }
    public decimal MarginUsed { get; internal set; }
    public decimal PeakEquity { get; internal set; }
    public decimal Equity => Cash + Positions.Sum(p => (decimal)p.UnrealizedPnl);
    public IReadOnlyList<Position> Positions { get; internal set; } = [];

    public SubPortfolio(string strategyId, decimal allocatedCapital)
    {
        StrategyId = strategyId;
        AllocatedCapital = allocatedCapital;
        Cash = allocatedCapital;
        PeakEquity = allocatedCapital;
    }
}
