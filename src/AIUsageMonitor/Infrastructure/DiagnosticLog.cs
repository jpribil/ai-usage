namespace AIUsageMonitor.Infrastructure;

internal sealed class DiagnosticLog : IDisposable
{
    private readonly StreamWriter? _writer;

    private DiagnosticLog(StreamWriter? writer) => _writer = writer;

    internal static DiagnosticLog CreateIfRequested(IEnumerable<string> args)
    {
        if (!args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
        {
            return new DiagnosticLog(null);
        }

        var path = Path.Combine(Path.GetTempPath(), "ai-usage-monitor.log");
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new DiagnosticLog(new StreamWriter(stream) { AutoFlush = true });
    }

    internal void Write(string message)
    {
        _writer?.WriteLine($"[{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}] {message}");
    }

    public void Dispose() => _writer?.Dispose();
}
