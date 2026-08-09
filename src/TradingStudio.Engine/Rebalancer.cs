using TradingStudio.Core.Engine;

namespace TradingStudio.Engine;

/// <summary>
/// 再平衡器: 比较 PortfolioTarget 与当前持仓，生成增量订单。
/// 不需要改动 ExecutionHandler — 输出仍然是 Order 对象。
///
/// 核心逻辑: Δ = target - current
/// - Δ > 0 → 买入 (Buy) |Δ| 手
/// - Δ < 0 → 卖出 (Sell) |Δ| 手
/// - Δ = 0 → 不操作
///
/// 方向翻转（多→空 或 空→多）：先平仓再反向开仓到目标量。
/// 详见 docs/design/18-trade-signal-portfolio-target-decoupling.md
/// </summary>
public class Rebalancer
{
    /// <summary>平仓阈值: 目标权重偏离 < 此值 → 不调仓（避免过度交易）</summary>
    public double RebalanceThreshold { get; init; } = 0.005;  // 0.5%

    /// <summary>补仓阈值: 只调超过此比例的偏差</summary>
    public double MinTradeWeight { get; init; } = 0.01;  // 1%

    /// <summary>最小交易手数</summary>
    public int MinTradeLots { get; init; } = 1;

    /// <summary>
    /// 生成订单以实现从 currentPositions 到 targets 的转换。
    /// 每个品种生成至多 2 个订单（平仓 + 开仓），避免过度交易。
    /// </summary>
    /// <param name="targets">目标持仓列表</param>
    /// <param name="currentPositions">当前持仓列表</param>
    /// <param name="strategyId">策略ID</param>
    /// <returns>需要提交的订单列表</returns>
    public List<Order> GenerateOrders(
        IReadOnlyList<PortfolioTarget> targets,
        IReadOnlyList<Position> currentPositions,
        string strategyId)
    {
        var orders = new List<Order>();

        // 构建当前持仓索引
        var currentByInst = new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase);
        foreach (var pos in currentPositions)
        {
            if (pos.StrategyId == strategyId && pos.Quantity != 0)
                currentByInst[pos.InstrumentId] = pos;
        }

        // 构建目标索引
        var targetByInst = new Dictionary<string, PortfolioTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
            targetByInst[t.InstrumentId] = t;

        // 处理所有目标品种
        var processedInstruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (instId, target) in targetByInst)
        {
            processedInstruments.Add(instId);
            currentByInst.TryGetValue(instId, out var current);
            var order = GenerateDeltaOrder(instId, target, current, strategyId);
            if (order != null) orders.Add(order);
        }

        return orders;
    }

    /// <summary>为单个品种生成 delta 订单</summary>
    private Order? GenerateDeltaOrder(
        string instrumentId,
        PortfolioTarget target,
        Position? current,
        string strategyId)
    {
        int currentQty = current?.Quantity ?? 0;
        int targetQty = target.TargetQuantity;

        // Δ = 目标 - 当前
        int delta = targetQty - currentQty;

        // 检查最小交易量（绝对值变化太小 → 不交易）
        if (Math.Abs(delta) < MinTradeLots)
            return null;

        // 方向翻转：当前持仓方向与目标方向相反
        bool needFlip = (currentQty > 0 && targetQty < 0) || (currentQty < 0 && targetQty > 0);

        if (needFlip)
        {
            // 平仓当前持仓
            return CreateCloseOrder(instrumentId, current!, target, strategyId);
        }

        if (delta > 0)
        {
            // 加多/加空：方向不变，只增量
            return new Order
            {
                InstrumentId = instrumentId,
                Direction = OrderDirection.Buy,
                Type = OrderType.Market,
                Quantity = delta,
                Tag = $"再平衡+{delta}",
                StrategyId = strategyId,
                PositionCreatedDate = current?.CreatedTime != default
                    ? DateOnly.FromDateTime(current!.CreatedTime.DateTime)
                    : default,
            };
        }
        else if (delta < 0)
        {
            // 减仓（不翻方向）
            return new Order
            {
                InstrumentId = instrumentId,
                Direction = OrderDirection.Sell,
                Type = OrderType.Market,
                Quantity = Math.Abs(delta),
                Tag = $"减仓{delta}",
                IsCloseOrder = true,
                StrategyId = strategyId,
                PositionCreatedDate = current?.CreatedTime != default
                    ? DateOnly.FromDateTime(current!.CreatedTime.DateTime)
                    : default,
            };
        }

        return null;
    }

    private static Order CreateCloseOrder(
        string instrumentId,
        Position current,
        PortfolioTarget target,
        string strategyId)
    {
        return new Order
        {
            InstrumentId = instrumentId,
            Direction = current.Quantity > 0 ? OrderDirection.Sell : OrderDirection.Buy,
            Type = OrderType.Market,
            Quantity = Math.Abs(current.Quantity),
            Tag = target.TargetQuantity == 0 ? "平仓(信号Flat)" : "平仓(方向翻转)",
            IsCloseOrder = true,
            StrategyId = strategyId,
            PositionCreatedDate = current.CreatedTime != default
                ? DateOnly.FromDateTime(current.CreatedTime.DateTime)
                : default,
        };
    }
}
