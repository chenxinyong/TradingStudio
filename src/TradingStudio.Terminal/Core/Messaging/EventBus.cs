using System.Collections.Concurrent;

namespace TradingStudio.Terminal.Core.Messaging;

/// <summary>
/// Lightweight pub/sub event bus for decoupled communication between modules.
/// Thread-safe. Subscribers receive typed messages.
/// Ported from CodeEditor's Editor.Core.Messaging.EventBus.
/// </summary>
public class EventBus
{
    private readonly ConcurrentDictionary<Type, List<object>> _handlers = new();

    /// <summary>
    /// Subscribe to messages of type T. Returns a disposable token that unsubscribes on Dispose.
    /// </summary>
    public IDisposable Subscribe<T>(Action<T> handler)
    {
        var type = typeof(T);
        var handlers = _handlers.GetOrAdd(type, _ => new List<object>());
        lock (handlers)
        {
            handlers.Add(handler);
        }

        return new Unsubscriber(() =>
        {
            if (_handlers.TryGetValue(type, out var list))
            {
                lock (list)
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                        _handlers.TryRemove(type, out _);
                }
            }
        });
    }

    /// <summary>
    /// Subscribe with async handler. Fire-and-forget on publish.
    /// </summary>
    public IDisposable Subscribe<T>(Func<T, Task> handler)
    {
        return Subscribe<T>(msg => _ = handler(msg));
    }

    /// <summary>
    /// Publish a message to all subscribers of type T.
    /// </summary>
    public void Publish<T>(T message)
    {
        if (_handlers.TryGetValue(typeof(T), out var handlers))
        {
            object[] snapshot;
            lock (handlers)
            {
                snapshot = handlers.ToArray();
            }

            foreach (var handler in snapshot)
            {
                ((Action<T>)handler)(message);
            }
        }
    }

    /// <summary>
    /// Publish a message and await all async handlers.
    /// </summary>
    public async Task PublishAsync<T>(T message)
    {
        if (_handlers.TryGetValue(typeof(T), out var handlers))
        {
            object[] snapshot;
            lock (handlers)
            {
                snapshot = handlers.ToArray();
            }

            foreach (var handler in snapshot)
            {
                if (handler is Func<T, Task> asyncHandler)
                    await asyncHandler(message);
                else
                    ((Action<T>)handler)(message);
            }
        }
    }

    /// <summary>
    /// Remove all subscriptions.
    /// </summary>
    public void Clear() => _handlers.Clear();

    private class Unsubscriber : IDisposable
    {
        private readonly Action _action;
        public Unsubscriber(Action action) => _action = action;
        public void Dispose() => _action();
    }
}
