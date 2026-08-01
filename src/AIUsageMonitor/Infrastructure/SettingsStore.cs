using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.Infrastructure;

internal sealed class SettingsStore(DiagnosticLog diagnosticLog)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    // Portable application: settings belong beside the executable, not in AppData.
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "settings.json");

    internal AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings().Normalize();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), SerializerOptions)
                ?? new AppSettings();
            return settings.Normalize();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            diagnosticLog.Write($"Unable to load settings: {exception.Message}");
            return new AppSettings().Normalize();
        }
    }

    internal void Save(AppSettings settings)
    {
        try
        {
            var normalized = settings.Normalize();
            File.WriteAllText(_path, JsonSerializer.Serialize(normalized, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnosticLog.Write($"Unable to save settings beside executable: {exception.Message}");
        }
    }
}
