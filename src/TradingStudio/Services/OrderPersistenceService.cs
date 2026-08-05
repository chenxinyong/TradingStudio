using TradingStudio.Core.Engine;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;

namespace TradingStudio.Services;

/// <summary>
/// 订单事件持久化 — 作为 IOrderEventSink 由 OrderEventPump 串行调用。
/// 5 秒定时刷盘，确保订单事件及时落库。
///
/// v3: 不再直接读 OrderOutbox.Reader，改为实现 IOrderEventSink。
///     OrderEventPump 是唯一消费者，PushService 和本服务均为 Sink。
///     架构: TradingEngine → OrderOutbox → OrderEventPump(唯一消费者)
///             → [EngineHubPushService.Sink, OrderPersistenceService.Sink]
/// </summary>
public class OrderPersistenceService : BackgroundService, IOrderEventSink
{
    private readonly IBarStore _store;
    private readonly Serilog.ILogger _log;
    private readonly List<OrderEvent> _batch = new(64);
    private readonly object _lock = new();

    public OrderPersistenceService(
        IBarStore store,
        Serilog.ILogger log)
    {
        _store = store;
        _log = log.ForContext<OrderPersistenceService>();
    }

    // ─── IOrderEventSink ───
    // 由 OrderEventPump 在单消费者线程中串行调用，不再与 PushService 竞争 OrderOutbox.Reader。

    /// <inheritdoc/>
    public Task HandleAsync(OrderEvent e, CancellationToken ct)
    {
        lock (_lock) _batch.Add(e);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("OrderPersistenceService started (Sink mode)");
        using var flushTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            while (await flushTimer.WaitForNextTickAsync(ct))
                await FlushAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Error(ex, "OrderPersistenceService crashed");
        }
        finally
        {
            await FlushAsync();
            _log.Information("OrderPersistenceService stopped — {Count} events persisted", _batch.Count);
        }
    }

    private async Task FlushAsync()
    {
        List<OrderEvent> snapshot;
        lock (_lock)
        {
            if (_batch.Count == 0) return;
            snapshot = new List<OrderEvent>(_batch);
            _batch.Clear();
        }

        try
        {
            if (_store is DuckDBStore duck)
                duck.WriteOrderEvents(snapshot);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to persist {Count} order events", snapshot.Count);
        }
    }
}
