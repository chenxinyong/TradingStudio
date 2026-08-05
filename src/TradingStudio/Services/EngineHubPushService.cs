using Microsoft.AspNetCore.SignalR;
using TradingStudio.Core.Engine;
using TradingStudio.Engine;

namespace TradingStudio.Services;

/// <summary>
/// SignalR 推送服务 — 监控引擎组件，实时推送到客户端。
///
/// v3: 作为 IOrderEventSink 由 OrderEventPump 串行调用，不再直读 OrderOutbox.Reader。
///     借鉴 StockSharp CtpMessageAdapter 回调泵模式 —— 消除双消费者竞态。
/// </summary>
public class EngineHubPushService : BackgroundService, IOrderEventSink
{
    private readonly IHubContext<EngineHub> _hub;
    private readonly PortfolioManager? _portfolio;
    private readonly TickSnapshot? _ticks;
    private readonly FeedbackMonitor? _feedback;
    private readonly StrategyContainer? _strategies;

    private readonly System.Threading.Channels.Channel<OrderEvent> _orderChannel
        = System.Threading.Channels.Channel.CreateBounded<OrderEvent>(256);

    public EngineHubPushService(
        IHubContext<EngineHub> hub,
        PortfolioManager? portfolio = null,
        TickSnapshot? ticks = null,
        FeedbackMonitor? feedback = null,
        StrategyContainer? strategies = null)
    {
        _hub = hub;
        _portfolio = portfolio;
        _ticks = ticks;
        _feedback = feedback;
        _strategies = strategies;
    }

    // ─── IOrderEventSink ───
    // 由 OrderEventPump 在单消费者线程中调用，不再与 PersistenceService 竞争 OrderOutbox.Reader。

    /// <inheritdoc/>
    public async Task HandleAsync(OrderEvent e, CancellationToken ct)
    {
        // 写入内部 Channel，解耦 Pump 线程和 SignalR 推送线程
        if (!_orderChannel.Writer.TryWrite(e))
        {
            // Channel 满时丢弃（256 已足够大，正常不应满）
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastPortfolio = DateTimeOffset.UtcNow;
        var lastAlerts = DateTimeOffset.UtcNow;

        // OrderEvent → SignalR 推送（从内部 Channel 消费，不再直读 OrderOutbox）
        var reader = _orderChannel.Reader;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var fill))
                {
                    await _hub.Clients.Group(fill.StrategyId ?? "")
                        .SendAsync("OrderUpdated", fill, ct);
                    await _hub.Clients.All
                        .SendAsync("OrderFlowUpdated", fill, ct);
                }
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);

            if (_ticks != null)
                await _hub.Clients.All.SendAsync("TickSnapshot", _ticks.GetAll(), ct);

            if (_portfolio != null)
                await _hub.Clients.All.SendAsync("PortfolioUpdated", new
                {
                    _portfolio.Equity, _portfolio.Cash, _portfolio.MarginUsed,
                    Positions = _portfolio.AllPositions,
                }, ct);

            if (_strategies != null && (DateTimeOffset.UtcNow - lastPortfolio).TotalSeconds >= 5)
            {
                lastPortfolio = DateTimeOffset.UtcNow;
                await _hub.Clients.All.SendAsync("StrategiesUpdated",
                    _strategies.GetAllSnapshots(), ct);
            }

            if (_feedback != null && (DateTimeOffset.UtcNow - lastAlerts).TotalSeconds >= 3)
            {
                lastAlerts = DateTimeOffset.UtcNow;
                var alerts = _feedback.CheckAlerts();
                foreach (var alert in alerts)
                    await _hub.Clients.Group(alert.StrategyId).SendAsync("Alert", alert, ct);
                await _hub.Clients.All.SendAsync("AlertsUpdated", alerts, ct);
            }
        }
    }
}
