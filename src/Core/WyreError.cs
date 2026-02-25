namespace Wyre.Core.Errors;

public enum WyreCodeType 
{ 
    Error, 
    Warning, 
    Popup 
}

public record WyreError
{
    public required string Code { get; init; }        // "WYRE-MESH-0003"
    public required WyreCodeType Type { get; init; }
    public required string Message { get; init; }     // human readable
    public string? Detail { get; init; }              // extra context
    public string? NodeId { get; init; }              // which node
    public string? SessionId { get; init; }           // which session
    public Exception? Exception { get; init; }        // inner exception if any
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
    
    public override string ToString() =>
        $"[{Code}] {Message}" +
        (Detail is not null ? $" — {Detail}" : "") +
        (NodeId is not null ? $" (node: {NodeId})" : "") +
        (SessionId is not null ? $" (session: {SessionId})" : "");
}

// Publish errors on the bus — any module can react
public record WyreErrorEvent(WyreError Error);
