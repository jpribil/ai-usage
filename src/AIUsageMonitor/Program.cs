using AIUsageMonitor.Core;
using AIUsageMonitor.Infrastructure;
using AIUsageMonitor.Services;
using AIUsageMonitor.UI;

namespace AIUsageMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Update.ApplyIfRequested(args))
        {
            return;
        }

        using var instance = new SingleInstanceGuard(AppMetadata.ApplicationId);
        if (!instance.IsPrimary)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var diagnosticLog = DiagnosticLog.CreateIfRequested(args);
        diagnosticLog.Write($"Started {AppMetadata.Title}.");

        var settingsStore = new SettingsStore(diagnosticLog);
        var settings = settingsStore.Load();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var credentials = new CredentialStore(diagnosticLog);
        var autostart = new AutostartService(diagnosticLog);
        autostart.HealExistingEntry();
        var polling = new UsagePollingService(
            new ClaudeUsageProvider(httpClient, credentials, diagnosticLog),
            new CodexUsageProvider(httpClient, credentials, diagnosticLog));
        Application.Run(new MainForm(settings, settingsStore, diagnosticLog, polling, new NtfyNotifier(httpClient, diagnosticLog), autostart, new RouterBalanceProvider(httpClient, diagnosticLog), new GitHubUpdateService(httpClient)));
    }
}
