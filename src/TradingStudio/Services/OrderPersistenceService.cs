using System.Threading.Channels;
using TradingStudio.Core.Engine;
using TradingStudio.Core.Storage;
using TradingStudio.Data.Storage;
using TradingStudio.Engine;

namespace TradingStudio.Services;

/// <summary>
/// 订单事件持久化 — 消费 FillChannel，写入 DuckDB + 转发到 OrderOutbox。
/// </summary>
public class OrderPersistenceService : BackgroundService
{
    private readonly ChannelReader<OrderEvent> _fillReader;
    private readonly ChannelWriter<OrderEvent> _outboxWriter;
    private readonly IBarStore _store;
    private readonly Serilog.ILogger _log;
    private readonly List<OrderEvent> _batch = new(64);

    public OrderPersistenceService(
        ExecutionHandler execution,
        IBarStore store,
        Serilog.ILogger log)
    {
        _fillReader = execution.FillChannel.Reader;
        _outboxWriter = execution.OrderOutbox.Writer;
        _store = store;
        _log = log.ForContext<OrderPersistenceService>();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.Information("OrderPersistenceService started");
        try
        {
            await foreach (var evt in _fillReader.ReadAllAsync(ct))
            {
                _batch.Add(evt);
                // 转发到 OrderOutbox（供 SignalR 推送）
                _outboxWriter.TryWrite(evt);

                if (_batch.Count >= 64)
                    await FlushAsync();
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
        if (_batch.Count == 0) return;
        try
        {
            if (_store is DuckDBStore duck)
                duck.WriteOrderEvents(_batch);
            // SQLite 不支持订单事件持久化
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to persist {Count} order events", _batch.Count);
        }
        _batch.Clear();
    }
}
