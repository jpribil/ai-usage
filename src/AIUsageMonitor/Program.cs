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
        DiagnosticLog? diagnosticLog = null;
        try
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

            diagnosticLog = DiagnosticLog.CreateIfRequested(args);
            Application.ThreadException += (_, eventArgs) => ReportFatal(eventArgs.Exception, diagnosticLog);
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            {
                ReportFatal(eventArgs.Exception, diagnosticLog);
                eventArgs.SetObserved();
            };
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
        catch (Exception exception)
        {
            ReportFatal(exception, diagnosticLog);
        }
        finally
        {
            diagnosticLog?.Dispose();
        }
    }

    private static void ReportFatal(Exception exception, DiagnosticLog? diagnosticLog)
    {
        var message = $"[{DateTimeOffset.UtcNow:O}] Fatal startup/UI error: {exception}";
        diagnosticLog?.Write(message);
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "ai-usage-monitor-startup-errors.log"), message + Environment.NewLine);
        }
        catch (IOException)
        {
            // Reporting failure must never cause another startup failure.
        }

        MessageBox.Show("Aplikaci se nepodařilo spustit. Podrobnosti jsou v %TEMP%\\ai-usage-monitor-startup-errors.log.",
            AppMetadata.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
