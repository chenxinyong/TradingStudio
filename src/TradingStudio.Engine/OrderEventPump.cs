using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingStudio.Core.Engine;

namespace TradingStudio.Engine;

/// <summary>
/// 订单事件消费者接口 — 由 OrderEventPump 串行调用，避免多消费者竞态。
/// 借鉴 StockSharp CtpMessageAdapter 的回调泵模式。
/// </summary>
public interface IOrderEventSink
{
    /// <summary>处理订单事件。异常由 Pump 隔离，不影响其他 Sink。</summary>
    Task HandleAsync(OrderEvent e, CancellationToken ct);
}

/// <summary>
/// 订单事件泵 — 借鉴 StockSharp CtpMessageAdapter.PumpCallbacks() 的单消费者模式。
///
/// 问题：OrderOutbox 原有两个并发消费者（EngineHubPushService + OrderPersistenceService），
/// ChannelReader.TryRead() 非线程安全，高并发下会丢事件。
///
/// 方案：OrderEventPump 是 OrderOutbox 的唯一消费者，串行分发给多个 IOrderEventSink。
/// 架构: TradingEngine → OrderOutbox → OrderEventPump(唯一消费者) → [Sink1, Sink2, ...]
/// </summary>
public class OrderEventPump : BackgroundService
{
    private readonly ChannelReader<OrderEvent> _reader;
    private readonly IOrderEventSink[] _sinks;
    private readonly ILogger _log;

    public OrderEventPump(
        ChannelReader<OrderEvent> reader,
        IEnumerable<IOrderEventSink> sinks,
        ILogger<OrderEventPump>? log = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _sinks = (sinks ?? throw new ArgumentNullException(nameof(sinks))).ToArray();
        _log = (ILogger?)log ?? NullLogger<OrderEventPump>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("OrderEventPump started — {SinkCount} sinks: {Sinks}",
            _sinks.Length, _sinks.Select(s => s.GetType().Name));

        try
        {
            while (await _reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_reader.TryRead(out var e))
                {
                    foreach (var sink in _sinks)
                    {
                        try
                        {
                            await sink.HandleAsync(e, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // 异常隔离：一个 Sink 崩溃不影响其他 Sink
                            _log.LogWarning(ex, "Sink {SinkType}.HandleAsync failed — event {EventType}:{StrategyId}",
                                sink.GetType().Name, e.Type, e.StrategyId);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "OrderEventPump crashed");
        }
        finally
        {
            _log.LogInformation("OrderEventPump stopped");
        }
    }
}
