using Microsoft.AspNetCore.SignalR;
using TradingStudio.Engine;

namespace TradingStudio.Services;

/// <summary>
/// SignalR 推送后台服务 — 监控引擎组件，实时推送到客户端。
/// </summary>
public class EngineHubPushService : BackgroundService
{
    private readonly IHubContext<EngineHub> _hub;
    private readonly PortfolioManager? _portfolio;
    private readonly TickSnapshot? _ticks;
    private readonly FeedbackMonitor? _feedback;
    private readonly ExecutionHandler? _execution;
    private readonly StrategyContainer? _strategies;

    public EngineHubPushService(
        IHubContext<EngineHub> hub,
        PortfolioManager? portfolio = null,
        TickSnapshot? ticks = null,
        FeedbackMonitor? feedback = null,
        ExecutionHandler? execution = null,
        StrategyContainer? strategies = null)
    {
        _hub = hub;
        _portfolio = portfolio;
        _ticks = ticks;
        _feedback = feedback;
        _execution = execution;
        _strategies = strategies;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastPortfolio = DateTimeOffset.UtcNow;
        var lastAlerts = DateTimeOffset.UtcNow;

        // 从引擎的 OrderOutbox 读取已处理的成交（引擎独占 FillChannel，
        // 处理完后写入 OrderOutbox，我们只读 OrderOutbox——避免双消费者竞态）
        if (_execution != null)
        {
            _ = Task.Run(async () =>
            {
                var reader = _execution.OrderOutbox.Reader;
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
        }

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
