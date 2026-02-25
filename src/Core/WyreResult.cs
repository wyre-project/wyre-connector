namespace Wyre.Core.Errors;

// Result type — never throw across module boundaries
public record WyreResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public WyreError? Error { get; init; }
    
    public static WyreResult<T> Ok(T value) => new() { Success = true, Value = value };
    public static WyreResult<T> Fail(WyreError error) => new() { Success = false, Error = error };
    
    public WyreResult<TOut> Map<TOut>(Func<T, TOut> mapper) =>
        Success ? WyreResult<TOut>.Ok(mapper(Value!)) : WyreResult<TOut>.Fail(Error!);
}

public record WyreResult
{
    public bool Success { get; init; }
    public WyreError? Error { get; init; }
    
    public static WyreResult Ok() => new() { Success = true };
    public static WyreResult Fail(WyreError error) => new() { Success = false, Error = error };
}
