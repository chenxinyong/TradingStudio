using TradingStudio.Core.Models;

namespace TradingStudio.Core.Engine;

/// <summary>
/// 订单路由 + 撮合接口。
/// 回测模式：模拟撮合，在 ProcessTick/ProcessBar 成交。
/// 实盘模式：发往 CTP TraderApi，等待回报。
/// </summary>
public interface IExecutionHandler
{
    /// <summary>提交订单，记录策略归属。可选传入 portfolio 做风控检查。</summary>
    OrderTicket Submit(Order order, string strategyId, Core.Risk.IPortfolioState? portfolio = null);

    /// <summary>取消订单</summary>
    bool Cancel(long orderId);

    /// <summary>Tick 到达后的撮合（Tick 回放 / 实盘模式）</summary>
    IReadOnlyList<OrderEvent> ProcessTick(TickRecord tick, string instrumentId, Future future);

    /// <summary>Bar 闭合后的撮合（Bar 回放模式 / 处理未触发的限价止损单）</summary>
    IReadOnlyList<OrderEvent> ProcessBar(Bar bar, Future future);

    /// <summary>所有活跃订单</summary>
    IReadOnlyList<Order> ActiveOrders { get; }

    /// <summary>按策略查询活跃订单</summary>
    IReadOnlyList<Order> GetActiveOrders(string strategyId);

    /// <summary>所有已成交/已拒绝/已取消订单</summary>
    IReadOnlyList<OrderEvent> OrderHistory { get; }
}
