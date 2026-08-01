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

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIUsageMonitor",
        "settings.json");

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
        var normalized = settings.Normalize();
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(normalized, SerializerOptions));
    }
}
