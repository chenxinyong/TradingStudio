using System.Threading.Channels;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;

namespace TradingStudio.Services;

/// <summary>
/// 订单事件持久化 — 消费 OrderOutbox (TradingEngine 处理完毕的 Fill)，写入 DuckDB。
/// 5 秒定时刷盘，确保订单事件及时落库。
///
/// 架构: CtpTraderBridge → FillChannel → TradingEngine(唯一消费者, portfolio处理)
///         → OrderOutbox → [SignalR推送, 本服务持久化]
/// </summary>
public class OrderPersistenceService : BackgroundService
{
    private readonly ChannelReader<OrderEvent> _outboxReader;
    private readonly IBarStore _store;
    private readonly Serilog.ILogger _log;
    private readonly List<OrderEvent> _batch = new(64);
    private readonly object _lock = new();

    public OrderPersistenceService(
        ExecutionHandler execution,
        IBarStore store,
        Serilog.ILogger log)
    {
        _outboxReader = execution.OrderOutbox.Reader;
        _store = store;
        _log = log.ForContext<OrderPersistenceService>();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("OrderPersistenceService started");
        using var flushTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            // 定时刷盘 + 事件消费并发
            var flushTask = Task.Run(async () =>
            {
                while (await flushTimer.WaitForNextTickAsync(ct))
                    await FlushAsync();
            }, ct);

            await foreach (var evt in _outboxReader.ReadAllAsync(ct))
            {
                lock (_lock) _batch.Add(evt);
            }
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
