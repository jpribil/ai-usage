using Microsoft.Win32;

namespace AIUsageMonitor.Infrastructure;

internal sealed class AutostartService(DiagnosticLog diagnosticLog)
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "AIUsageMonitor";

    internal bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                diagnosticLog.Write($"Unable to read autostart setting: {exception.Message}");
                return false;
            }
        }
    }

    internal void HealExistingEntry()
    {
        if (IsEnabled)
        {
            SetEnabled(true);
        }
    }

    internal void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            diagnosticLog.Write($"Unable to update autostart setting: {exception.Message}");
        }
    }
}
