using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.Infrastructure;

namespace AIUsageMonitor.Services;

internal sealed record ServicePollResult(UsageData? Data, PollError? Error)
{
    internal static ServicePollResult Success(UsageData data) => new(data, null);
    internal static ServicePollResult Failure(PollError error) => new(null, error);
}

internal interface IUsageProvider
{
    Task<ServicePollResult> PollAsync(CancellationToken cancellationToken);
}

internal sealed class ClaudeUsageProvider(HttpClient httpClient, CredentialStore credentials, DiagnosticLog diagnosticLog) : IUsageProvider
{
    internal const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    public async Task<ServicePollResult> PollAsync(CancellationToken cancellationToken)
    {
        var candidates = await credentials.FindClaudeCredentialsAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return ServicePollResult.Failure(PollError.NoCredentials);
        }

        foreach (var candidate in candidates)
        {
            var current = candidate;
            if (current.IsExpired)
            {
                await credentials.RefreshClaudeAsync(current.Source, cancellationToken);
                current = (await credentials.FindClaudeCredentialsAsync(cancellationToken))
                    .FirstOrDefault(item => item.Source == candidate.Source) ?? current;
                if (current.IsExpired)
                {
                    continue;
                }
            }

            var result = await PollWithCredentialAsync(current, cancellationToken);
            if (result.Error != PollError.AuthRequired)
            {
                return result;
            }

            await credentials.RefreshClaudeAsync(current.Source, cancellationToken);
            var refreshed = (await credentials.FindClaudeCredentialsAsync(cancellationToken))
                .FirstOrDefault(item => item.Source == current.Source);
            if (refreshed is not null && !refreshed.IsExpired)
            {
                result = await PollWithCredentialAsync(refreshed, cancellationToken);
                if (result.Error != PollError.AuthRequired)
                {
                    return result;
                }
            }
        }

        return ServicePollResult.Failure(candidates.All(item => item.IsExpired) ? PollError.TokenExpired : PollError.AuthRequired);
    }

    private async Task<ServicePollResult> PollWithCredentialAsync(ClaudeCredential credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ServicePollResult.Failure(PollError.AuthRequired);
            }

            UsageData? primaryData = null;
            if (response.IsSuccessStatusCode)
            {
                primaryData = TryParsePrimary(await response.Content.ReadAsStringAsync(cancellationToken));
            }

            if (primaryData is { } data && (data.Session.Available || data.Weekly.Available))
            {
                return ServicePollResult.Success(primaryData);
            }

            return await PollHeadersFallbackAsync(credential, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            diagnosticLog.Write($"Claude usage request failed: {exception.Message}");
            return await PollHeadersFallbackAsync(credential, cancellationToken);
        }
        catch (JsonException exception)
        {
            diagnosticLog.Write($"Claude usage response was invalid: {exception.Message}");
            return await PollHeadersFallbackAsync(credential, cancellationToken);
        }
    }

    private async Task<ServicePollResult> PollHeadersFallbackAsync(ClaudeCredential credential, CancellationToken cancellationToken)
    {
        foreach (var model in new[] { "claude-3-haiku-20240307", "claude-haiku-4-5-20251001" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Content = new StringContent($"{{\"model\":\"{model}\",\"max_tokens\":1,\"messages\":[{{\"role\":\"user\",\"content\":\".\"}}]}}", Encoding.UTF8, "application/json");
            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return ServicePollResult.Failure(PollError.AuthRequired);
                }

                var parsed = TryParseHeaders(response.Headers);
                if (parsed is not null)
                {
                    return ServicePollResult.Success(parsed);
                }
            }
            catch (HttpRequestException exception)
            {
                diagnosticLog.Write($"Claude rate-limit header fallback failed: {exception.Message}");
            }
        }

        return ServicePollResult.Failure(PollError.RequestFailed);
    }

    internal static UsageData? TryParsePrimary(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var session = ParseAnthropicSection(root, "five_hour");
        var weekly = ParseAnthropicSection(root, "seven_day");
        return session.Available || weekly.Available ? new UsageData(session, weekly) : null;
    }

    private static UsageSection ParseAnthropicSection(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Object ||
            !section.TryGetProperty("utilization", out var utilization) || !utilization.TryGetDouble(out var percentage))
        {
            return UsageSection.Unavailable;
        }

        DateTimeOffset? resetsAt = null;
        if (section.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            resetsAt = parsed;
        }
        return new UsageSection(percentage, resetsAt, true);
    }

    private static UsageData? TryParseHeaders(HttpResponseHeaders headers)
    {
        var session = ParseHeaderSection(headers, "anthropic-ratelimit-unified-5h-utilization", "anthropic-ratelimit-unified-5h-reset");
        var weekly = ParseHeaderSection(headers, "anthropic-ratelimit-unified-7d-utilization", "anthropic-ratelimit-unified-7d-reset");
        if (!session.Available && !weekly.Available)
        {
            return null;
        }

        if (session.Percentage == 0 && weekly.Percentage == 0 && Header(headers, "anthropic-ratelimit-unified-status") == "rejected")
        {
            if (Header(headers, "anthropic-ratelimit-unified-representative-claim") == "five_hour")
            {
                session = session with { Percentage = 100 };
            }
            else if (Header(headers, "anthropic-ratelimit-unified-representative-claim") == "seven_day")
            {
                weekly = weekly with { Percentage = 100 };
            }
        }

        return new UsageData(session, weekly);
    }

    private static UsageSection ParseHeaderSection(HttpResponseHeaders headers, string utilizationName, string resetName)
    {
        var utilization = Header(headers, utilizationName);
        if (!double.TryParse(utilization, CultureInfo.InvariantCulture, out var fraction))
        {
            return UsageSection.Unavailable;
        }
        var reset = Header(headers, resetName);
        var resetsAt = long.TryParse(reset, CultureInfo.InvariantCulture, out var seconds)
            ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        return new UsageSection(fraction * 100, resetsAt, true);
    }

    private static string? Header(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}

internal sealed class CodexUsageProvider(HttpClient httpClient, CredentialStore credentials, DiagnosticLog diagnosticLog) : IUsageProvider
{
    internal const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

    public async Task<ServicePollResult> PollAsync(CancellationToken cancellationToken)
    {
        var credential = credentials.FindCodexCredential();
        if (credential is null)
        {
            return ServicePollResult.Failure(PollError.NoCredentials);
        }

        var result = await PollWithCredentialAsync(credential, cancellationToken);
        if (result.Error != PollError.AuthRequired)
        {
            return result;
        }

        await credentials.RefreshCodexAsync(cancellationToken);
        credential = credentials.FindCodexCredential();
        return credential is null ? ServicePollResult.Failure(PollError.AuthRequired) : await PollWithCredentialAsync(credential, cancellationToken);
    }

    private async Task<ServicePollResult> PollWithCredentialAsync(CodexCredential credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.UserAgent.ParseAdd("codex-cli");
        if (!string.IsNullOrWhiteSpace(credential.AccountId))
        {
            request.Headers.Add("ChatGPT-Account-Id", credential.AccountId);
        }
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ServicePollResult.Failure(PollError.AuthRequired);
            }
            if (!response.IsSuccessStatusCode)
            {
                return ServicePollResult.Failure(PollError.RequestFailed);
            }
            return ServicePollResult.Success(Parse(await response.Content.ReadAsStringAsync(cancellationToken)));
        }
        catch (HttpRequestException exception)
        {
            diagnosticLog.Write($"Codex usage request failed: {exception.Message}");
            return ServicePollResult.Failure(PollError.RequestFailed);
        }
        catch (JsonException exception)
        {
            diagnosticLog.Write($"Codex usage response was invalid: {exception.Message}");
            return ServicePollResult.Failure(PollError.RequestFailed);
        }
    }

    internal static UsageData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rateLimit = root.TryGetProperty("rate_limit", out var rateLimitElement) ? rateLimitElement : default;
        UsageSection? session = null;
        UsageSection? weekly = null;
        AssignWindow(rateLimit, "primary_window", true, ref session, ref weekly);
        AssignWindow(rateLimit, "secondary_window", false, ref session, ref weekly);

        uint? credits = null;
        if (root.TryGetProperty("rate_limit_reset_credits", out var creditElement) &&
            creditElement.TryGetProperty("available_count", out var available) && available.TryGetUInt32(out var count))
        {
            credits = count;
        }
        return new UsageData(session ?? UsageSection.Unavailable, weekly ?? UsageSection.Unavailable, credits);
    }

    private static void AssignWindow(JsonElement rateLimit, string property, bool positionIsSession, ref UsageSection? session, ref UsageSection? weekly)
    {
        if (rateLimit.ValueKind != JsonValueKind.Object || !rateLimit.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (!window.TryGetProperty("used_percent", out var percentage) || !percentage.TryGetDouble(out var usedPercentage))
        {
            return;
        }
        var resetsAt = window.TryGetProperty("reset_at", out var reset) && reset.TryGetInt64(out var seconds)
            ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        var section = new UsageSection(usedPercentage, resetsAt, true);
        var isSession = !window.TryGetProperty("limit_window_seconds", out var duration) || !duration.TryGetInt64(out var durationSeconds)
            ? positionIsSession
            : durationSeconds <= 86_400;
        if (isSession && session is null)
        {
            session = section;
        }
        else if (!isSession && weekly is null)
        {
            weekly = section;
        }
    }
}
