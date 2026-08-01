namespace AIUsageMonitor.Infrastructure;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    internal SingleInstanceGuard(string applicationId)
    {
        _mutex = new Mutex(true, $"Local\\{applicationId}", out var createdNew);
        IsPrimary = createdNew;
    }

    internal bool IsPrimary { get; }

    public void Dispose()
    {
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
