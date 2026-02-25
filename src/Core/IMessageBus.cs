namespace Wyre.Core.Messaging;

public interface IMessageBus
{
    void Publish<T>(T message) where T : class;
    
    Task<TResponse> RequestAsync<TRequest, TResponse>(
        TRequest request, 
        CancellationToken ct = default)
        where TRequest : class 
        where TResponse : class;
        
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
    
    IDisposable Subscribe<T>(Func<T, Task> handler) where T : class;
    
    void Handle<TRequest, TResponse>(
        Func<TRequest, CancellationToken, Task<TResponse>> handler)
        where TRequest : class 
        where TResponse : class;
}
