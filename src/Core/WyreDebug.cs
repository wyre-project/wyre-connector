using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Wyre.Core.Diagnostics;

public record DebugEntry(
    DateTimeOffset Timestamp,
    string Component,
    string Message,
    string Caller,
    string File,
    int Line,
    int ThreadId
);

public interface IDebugSink
{
    void Write(DebugEntry entry);
}

public static class WyreDebug
{
    public static bool Enabled { get; private set; }
    private static IDebugSink? _sink;

    public static void Initialize(bool enabled, IDebugSink? sink = null)
    {
        Enabled = enabled;
        _sink = sink;
    }
    
    // Zero cost when disabled — JIT eliminates these calls entirely
    [Conditional("DEBUG_ENABLED")]
    public static void Log(string component, string message, 
        [CallerMemberName] string caller = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (!Enabled || _sink == null) return;
        
        var entry = new DebugEntry(
            Timestamp: DateTimeOffset.UtcNow,
            Component: component,
            Message: message,
            Caller: caller,
            File: Path.GetFileName(file),
            Line: line,
            ThreadId: Environment.CurrentManagedThreadId
        );
        
        // Write to debug sink — configurable: console, file, overlay, all
        _sink.Write(entry);
    }
    
    // Scoped timing — wraps an operation and logs its duration
    public static IDisposable Time(string component, string operation)
        => Enabled 
            ? new DebugTimer(component, operation) 
            : NullDisposable.Instance;
    
    // Bus traffic tracing — logs every message published
    public static void TraceMessage<T>(T message) where T : class
    {
        if (!Enabled) return;
        Log("BUS", $"→ {typeof(T).Name}: {JsonSerializer.Serialize(message)}");
    }

    private sealed class DebugTimer : IDisposable
    {
        private readonly string _component;
        private readonly string _operation;
        private readonly Stopwatch _sw;

        public DebugTimer(string component, string operation)
        {
            _component = component;
            _operation = operation;
            _sw = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _sw.Stop();
            Log(_component, $"{_operation} completed in {_sw.ElapsedMilliseconds}ms");
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
