using System.Net.Http.Headers;
using System.Text.Json;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.Services;

internal sealed record UpdateCheckResult(Version? LatestVersion, bool IsNewer, string? Error);

internal sealed class GitHubUpdateService(HttpClient httpClient)
{
    internal const string Repository = "jpribil/ai-usage";

    internal async Task<UpdateCheckResult> CheckAsync(string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd(AppMetadata.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(null, false, $"HTTP {(int)response.StatusCode}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var tag = document.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v');
            if (!Version.TryParse(tag, out var latest)) return new(null, false, "Neplatný tag releasu");
            return new(latest, latest > Version.Parse(AppMetadata.Version), null);
        }
        catch (HttpRequestException exception) { return new(null, false, exception.Message); }
        catch (JsonException exception) { return new(null, false, exception.Message); }
    }
}
