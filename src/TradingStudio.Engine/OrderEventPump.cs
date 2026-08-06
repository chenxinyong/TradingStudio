using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingStudio.Core.Engine;

namespace TradingStudio.Engine;

/// <summary>
/// 订单事件泵 — 借鉴 StockSharp CtpMessageAdapter.PumpCallbacks()。
/// 单消费者从 OrderOutbox 读取, 串行分发给多个 IOrderEventSink。
/// </summary>
public class OrderEventPump : IHostedService
{
    private readonly ChannelReader<OrderEvent> _reader;
    private readonly List<IOrderEventSink> _sinks;
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public OrderEventPump(ChannelReader<OrderEvent> reader, IEnumerable<IOrderEventSink> sinks,
        ILogger<OrderEventPump> log)
    {
        _reader = reader;
        _sinks = sinks.ToList();
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunLoop(_cts.Token), ct);
        _log.LogInformation("OrderEventPump started, {Count} sinks", _sinks.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_loop != null) await _loop;
        _log.LogInformation("OrderEventPump stopped");
    }

    private async Task RunLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _reader.ReadAllAsync(ct))
            {
                foreach (var sink in _sinks)
                {
                    try { await sink.HandleAsync(evt, ct); }
                    catch (Exception ex) { _log.LogError(ex, "Sink {Sink} failed", sink.GetType().Name); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

/// <summary>订单事件消费者接口 — StockSharp PumpCallbacks 模式</summary>
public interface IOrderEventSink
{
    Task HandleAsync(OrderEvent e, CancellationToken ct);
}
