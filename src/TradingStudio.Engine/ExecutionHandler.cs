using TradingStudio.Core.Engine;
using TradingStudio.Core.Models;

namespace TradingStudio.Engine;

/// <summary>
/// 订单撮合引擎 — 回测和实盘共用。
/// 多策略订单队列 + 优先级排序 + 流动性约束。
/// </summary>
public class ExecutionHandler : IExecutionHandler
{
    private readonly List<Order> _activeOrders = new();
    private readonly List<OrderEvent> _orderHistory = new();
    private readonly Dictionary<string, int> _strategyPriorities = new();
    private readonly Dictionary<string, int> _lastCumulativeVolume = new();

    public IReadOnlyList<Order> ActiveOrders => _activeOrders;
    public IReadOnlyList<Order> GetActiveOrders(string strategyId) =>
        _activeOrders.Where(o => o.StrategyId == strategyId).ToList();
    public IReadOnlyList<OrderEvent> OrderHistory => _orderHistory;

    public OrderTicket Submit(Order order, string strategyId)
    {
        // Phase 2a 实现
        throw new NotImplementedException("Phase 2a: 实现订单提交");
    }

    public bool Cancel(long orderId)
    {
        // Phase 2a 实现
        throw new NotImplementedException("Phase 2a: 实现订单取消");
    }

    public IReadOnlyList<OrderEvent> ProcessTick(TickRecord tick, string instrumentId, Future future)
    {
        // Phase 2b 实现
        return [];
    }

    public IReadOnlyList<OrderEvent> ProcessBar(Bar bar, Future future)
    {
        // Phase 2a 实现
        return [];
    }

    // 增量成交量 = 当前 Tick 累计量 - 上一 Tick 累计量
    private int GetIncrementalVolume(string instrumentId, int currentCumulative)
    {
        if (!_lastCumulativeVolume.TryGetValue(instrumentId, out var last))
            last = 0;
        int delta = currentCumulative - last;
        if (delta < 0) delta = currentCumulative;  // 跨日重置保护
        _lastCumulativeVolume[instrumentId] = currentCumulative;
        return delta;
    }
}
