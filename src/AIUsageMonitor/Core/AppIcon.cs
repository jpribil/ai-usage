namespace AIUsageMonitor.Core;

internal static class AppIcon
{
    private static readonly Lazy<Icon> Cached = new(() =>
        Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application);

    internal static Icon Instance => Cached.Value;
}
