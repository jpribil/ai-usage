using System.Reflection;

namespace AIUsageMonitor.Core;

internal static class AppMetadata
{
    internal const string ApplicationId = "AIUsageMonitor.CSharp.WinForms";
    internal const string ProductName = "AI Usage Monitor";

    internal static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? "0.0.0";

    internal static string DisplayVersion => Version.EndsWith(".0", StringComparison.Ordinal)
        ? Version[..^2]
        : Version;

    internal static string Title => $"{ProductName} {DisplayVersion}";
    internal static string UserAgent => $"ai-usage-monitor/{Version}";
}
