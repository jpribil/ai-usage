namespace AIUsageMonitor.Infrastructure;

internal static class Update
{
    internal static bool ApplyIfRequested(string[] args)
    {
        // The dedicated update helper is wired here before any UI or singleton setup.
        // Its rollback-safe file-swap implementation is added with the update subsystem.
        return args.Length > 0 && args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase);
    }
}
