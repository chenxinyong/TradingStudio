using Microsoft.AspNetCore.SignalR;

namespace TradingStudio;

/// <summary>
/// SignalR Hub — 引擎与 UI 之间唯一的实时通道。
/// 按策略/品种分组推送: 客户端可订阅策略事件或品种 Bar 推送。
/// </summary>
public class EngineHub : Hub
{
    public async Task SubscribeStrategy(string strategyId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, strategyId);

    public async Task UnsubscribeStrategy(string strategyId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, strategyId);

    /// <summary>订阅指定品种的实时 Bar 推送</summary>
    public async Task SubscribeBars(string instrumentId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"bars:{instrumentId}");

    /// <summary>取消订阅品种 Bar 推送</summary>
    public async Task UnsubscribeBars(string instrumentId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"bars:{instrumentId}");
}
