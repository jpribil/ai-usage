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

    internal static string DisplayVersion => Version.Split('.') is [var major, var minor, ..]
        ? $"{major}.{minor.PadLeft(2, '0')}"
        : Version;

    internal static string Title => $"{ProductName} {DisplayVersion}";
    internal static string UserAgent => $"ai-usage-monitor/{Version}";
}
