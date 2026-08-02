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

            var json = File.ReadAllText(_path);
            var settings = (JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings()).Normalize();
            if (json.Contains("\"github_update_token_protected\"", StringComparison.Ordinal))
            {
                Save(settings);
            }

            return settings;
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
