using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using TradingStudio.Terminal.Core.Messaging;

namespace TradingStudio.Terminal.Services;

/// <summary>
/// SignalR 引擎客户端 — 单例，管理连接生命周期和事件分发。
/// 双通道：C# event（直接订阅）+ EventBus（pub/sub 解耦）。
/// </summary>
public enum ConnectionState { Disconnected, Connecting, Connected, Degraded }

public class EngineHubClient : IAsyncDisposable
{
    private readonly ILogger<EngineHubClient> _log;
    private readonly EventBus? _eventBus;
    private HubConnection? _connection;
    private readonly string _url;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public event Action<ConnectionState>? StateChanged;

    // ── 服务端推送事件 (C# event — 向后兼容) ──
    public event Action<IReadOnlyList<TickSnapshotItem>>? TickSnapshotReceived;
    public event Action<PortfolioUpdatedPayload>? PortfolioUpdated;
    public event Action<IReadOnlyList<StrategySnapshot>>? StrategiesUpdated;
    public event Action<OrderEvent>? OrderUpdated;
    public event Action<MonitorAlert>? AlertReceived;

    public EngineHubClient(string url = "http://localhost:5199/hubs/engine",
                           ILogger<EngineHubClient>? log = null,
                           EventBus? eventBus = null)
    {
        _url = url;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EngineHubClient>.Instance;
        _eventBus = eventBus;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(ConnectionState.Connecting);

        _connection = new HubConnectionBuilder()
            .WithUrl(_url)
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        // ── 服务端推送 → 双通道分发 ──
        _connection.On<IReadOnlyList<TickSnapshotItem>>("TickSnapshot", items =>
        {
            try
            {
                TickSnapshotReceived?.Invoke(items);
                _eventBus?.Publish(new TickBatchReceived(items));
            }
            catch (Exception ex) { _log.LogError(ex, "TickSnapshot handler error"); }
        });

        _connection.On<PortfolioUpdatedPayload>("PortfolioUpdated", p =>
        {
            try
            {
                PortfolioUpdated?.Invoke(p);
                _eventBus?.Publish(new PortfolioChanged(p));
            }
            catch (Exception ex) { _log.LogError(ex, "PortfolioUpdated handler error"); }
        });

        _connection.On<IReadOnlyList<StrategySnapshot>>("StrategiesUpdated", s =>
        {
            try
            {
                StrategiesUpdated?.Invoke(s);
                _eventBus?.Publish(new StrategiesChanged(s));
            }
            catch (Exception ex) { _log.LogError(ex, "StrategiesUpdated handler error"); }
        });

        _connection.On<OrderEvent>("OrderFlowUpdated", o =>
        {
            try
            {
                OrderUpdated?.Invoke(o);
                _eventBus?.Publish(new OrderFlowReceived(o));
            }
            catch (Exception ex) { _log.LogError(ex, "OrderUpdated handler error"); }
        });

        _connection.On<MonitorAlert>("AlertsUpdated", a =>
        {
            try
            {
                AlertReceived?.Invoke(a);
                _eventBus?.Publish(new AlertOccurred(a));
            }
            catch (Exception ex) { _log.LogError(ex, "Alert handler error"); }
        });

        _connection.Reconnecting += _ =>
        {
            SetState(ConnectionState.Connecting);
            return Task.CompletedTask;
        };

        _connection.Reconnected += _ =>
        {
            SetState(ConnectionState.Connected);
            return Task.CompletedTask;
        };

        _connection.Closed += _ =>
        {
            SetState(ConnectionState.Disconnected);
            return Task.CompletedTask;
        };

        try
        {
            await _connection.StartAsync(ct);
            SetState(ConnectionState.Connected);
            _log.LogInformation("Connected to engine at {Url}", _url);
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Disconnected);
            _log.LogWarning(ex, "Failed to connect to engine. Degraded mode — using health.json");
        }
    }

    public async Task SubscribeStrategyAsync(string strategyId)
    {
        if (_connection?.State == HubConnectionState.Connected)
            await _connection.InvokeAsync("SubscribeStrategy", strategyId);
    }

    private void SetState(ConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
        _eventBus?.Publish(new EngineConnectionChanged(state));
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null) await _connection.DisposeAsync();
    }

    /// <summary>SignalR 重试策略：1s→5s→10s→30s→30s...</summary>
    private class RetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) => ctx.PreviousRetryCount switch
        {
            0 => TimeSpan.FromSeconds(1),
            1 => TimeSpan.FromSeconds(5),
            2 => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(30),
        };
    }
}

// ── 轻量负载类型（避免客户端引用 Engine 项目）──

public record TickSnapshotItem(string InstrumentId, double LastPrice, long Volume,
    double BidPrice1, double AskPrice1, double OpenInterest, DateTimeOffset UpdateTime);

public record PortfolioUpdatedPayload(decimal Equity, decimal Cash, decimal MarginUsed,
    IReadOnlyList<PositionPayload> Positions);

public record PositionPayload(string InstrumentId, int Quantity, decimal AvgPrice,
    double MarketPrice, double UnrealizedPnl, decimal Margin, string StrategyId);

public record StrategySnapshot(string StrategyId, string StrategyType, string Status,
    IReadOnlyList<string> Instruments, decimal AllocatedCapital, decimal CurrentEquity,
    int PositionCount, int ActiveOrderCount);

public record OrderEvent(long OrderId, string InstrumentId, string StrategyId,
    string Direction, int Quantity, int OrderQty, int FilledQty, string Type,
    decimal FillPrice, decimal Fee, decimal Slippage, string? Message, DateTimeOffset Time);

public record MonitorAlert(string Type, string StrategyId, string Message,
    string Severity, DateTimeOffset Timestamp);

// ── EventBus 消息类型（SignalR → EventBus）──

public record TickBatchReceived(IReadOnlyList<TickSnapshotItem> Items);
public record PortfolioChanged(PortfolioUpdatedPayload Portfolio);
public record StrategiesChanged(IReadOnlyList<StrategySnapshot> Strategies);
public record OrderFlowReceived(OrderEvent Order);
public record AlertOccurred(MonitorAlert Alert);
public record EngineConnectionChanged(ConnectionState State);
