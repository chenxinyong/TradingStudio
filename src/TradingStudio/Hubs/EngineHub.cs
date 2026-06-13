using Microsoft.AspNetCore.SignalR;

namespace TradingStudio;

/// <summary>
/// SignalR Hub — 引擎与 UI 之间唯一的实时通道。
///
/// 信号流:
///   UI → Invoke("SubscribeStrategy", id) → Server 加入策略组
///   引擎事件 → Clients.Group(id).SendAsync("Alert", ...) → UI 收到
///
/// 优势:
///   - 内置自动重连（指数退避）
///   - 按策略分组推送（不用客户端过滤）
///   - 双向（Hub Method 可返回值，替代部分 REST 查询）
/// </summary>
public class EngineHub : Hub
{
    /// <summary>UI 订阅指定策略的推送（告警 + 订单更新）</summary>
    public async Task SubscribeStrategy(string strategyId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, strategyId);
    }

    /// <summary>UI 取消订阅</summary>
    public async Task UnsubscribeStrategy(string strategyId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, strategyId);
    }

    /// <summary>拉取引擎总览快照（替代多个 REST GET）</summary>
    public Task<EngineSnapshot> GetSnapshot()
    {
        // Phase 3: 从引擎组件获取完整快照
        return Task.FromResult(new EngineSnapshot { Status = "Phase 3" });
    }

    /// <summary>连接建立</summary>
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    /// <summary>连接断开（自动清理组）</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>引擎总览快照（SignalR Hub Method 返回）</summary>
public class EngineSnapshot
{
    public string Status { get; init; } = "";
    public DateTimeOffset ServerTime { get; init; } = DateTimeOffset.Now;
    public string Uptime { get; init; } = "";
    public string Session { get; init; } = "";
    public int QuoteCount { get; init; }
    public int BarCount { get; init; }
    public int ReconnectCount { get; init; }
    public int ActiveStrategyCount { get; init; }
    public int ActiveOrderCount { get; init; }
    public decimal TotalEquity { get; init; }
}
