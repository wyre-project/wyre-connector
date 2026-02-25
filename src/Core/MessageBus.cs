using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Wyre.Core.Messaging;

public sealed class MessageBus : IMessageBus
{
    private readonly ILogger<MessageBus> _logger;
    
    // Type-keyed subscriber lists
    private readonly ConcurrentDictionary<Type, Delegate[]> _subscribers = new();
    
    // Type-keyed request handlers (one handler per request type)
    private readonly ConcurrentDictionary<Type, Delegate> _handlers = new();

    public MessageBus(ILogger<MessageBus> logger)
    {
        _logger = logger;
    }
    
    public void Publish<T>(T message) where T : class
    {
        if (!_subscribers.TryGetValue(typeof(T), out var handlers)) return;
        
        foreach (var handler in handlers)
        {
            switch (handler)
            {
                case Action<T> sync:
                    try
                    {
                        sync(message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Synchronous subscriber threw an exception handling message of type {MessageType}", typeof(T).Name);
                    }
                    break;
                case Func<T, Task> async_:
                    // Fire and forget with error isolation per subscriber
                    _ = async_.Invoke(message).ContinueWith(t =>
                        _logger.LogError(t.Exception, "Asynchronous subscriber threw an exception handling message of type {MessageType}", typeof(T).Name), 
                        TaskContinuationOptions.OnlyOnFaulted);
                    break;
            }
        }
    }
    
    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        _subscribers.AddOrUpdate(
            typeof(T),
            _ => new Delegate[] { handler },
            (_, existing) => existing.Append(handler).ToArray());
            
        return new Subscription(() => 
        {
            _subscribers.AddOrUpdate(
                typeof(T),
                _ => Array.Empty<Delegate>(),
                (_, existing) => existing.Where(h => h != (Delegate)handler).ToArray());
        });
    }

    public IDisposable Subscribe<T>(Func<T, Task> handler) where T : class
    {
        _subscribers.AddOrUpdate(
            typeof(T),
            _ => new Delegate[] { handler },
            (_, existing) => existing.Append(handler).ToArray());
            
        return new Subscription(() => 
        {
            _subscribers.AddOrUpdate(
                typeof(T),
                _ => Array.Empty<Delegate>(),
                (_, existing) => existing.Where(h => h != (Delegate)handler).ToArray());
        });
    }
    
    public void Handle<TReq, TRes>(Func<TReq, CancellationToken, Task<TRes>> handler)
        where TReq : class where TRes : class
    {
        if (!_handlers.TryAdd(typeof(TReq), handler))
            throw new InvalidOperationException(
                $"Handler already registered for {typeof(TReq).Name}. " +
                $"Only one handler per request type allowed.");
    }
    
    public async Task<TRes> RequestAsync<TReq, TRes>(TReq request, CancellationToken ct = default)
        where TReq : class where TRes : class
    {
        if (!_handlers.TryGetValue(typeof(TReq), out var handler))
            throw new InvalidOperationException(
                $"No handler registered for {typeof(TReq).Name}. " +
                $"Is the required module loaded?");
        
        return await ((Func<TReq, CancellationToken, Task<TRes>>)handler)(request, ct);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private bool _disposed;

        public Subscription(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _unsubscribe();
            _disposed = true;
        }
    }
}
