using Microsoft.AspNetCore.SignalR;

namespace TradingStudio;

/// <summary>
/// SignalR Hub — 引擎与 UI 之间唯一的实时通道。
/// 按策略分组推送: UI SubscribeStrategy(id) → 引擎事件只推给该策略的订阅者。
/// </summary>
public class EngineHub : Hub
{
    public async Task SubscribeStrategy(string strategyId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, strategyId);

    public async Task UnsubscribeStrategy(string strategyId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, strategyId);
}
