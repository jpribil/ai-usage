using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIUsageMonitor.Infrastructure;

namespace AIUsageMonitor.Services;

internal sealed record RouterBalances(decimal? OpenRouterUsd, decimal? NanoGptUsd, string? OpenRouterError, string? NanoGptError);

internal sealed class RouterBalanceProvider(HttpClient httpClient, DiagnosticLog diagnosticLog)
{
    internal async Task<RouterBalances> GetAsync(string? openRouterKey, string? nanoGptKey, CancellationToken cancellationToken)
    {
        var openTask = GetOpenRouterAsync(openRouterKey, cancellationToken);
        var nanoTask = GetNanoGptAsync(nanoGptKey, cancellationToken);
        await Task.WhenAll(openTask, nanoTask);
        var open = await openTask; var nano = await nanoTask;
        return new RouterBalances(open.Amount, nano.Amount, open.Error, nano.Error);
    }

    private async Task<(decimal? Amount, string? Error)> GetOpenRouterAsync(string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) return (null, null);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var data = document.RootElement.GetProperty("data");
            var total = data.GetProperty("total_credits").GetDecimal();
            var usage = data.GetProperty("total_usage").GetDecimal();
            return (total - usage, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException)
        {
            diagnosticLog.Write($"OpenRouter balance request failed: {exception.Message}"); return (null, "!");
        }
    }

    private async Task<(decimal? Amount, string? Error)> GetNanoGptAsync(string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) return (null, null);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://nano-gpt.com/api/check-balance");
            request.Headers.Add("x-api-key", key);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var value = document.RootElement.GetProperty("usd_balance").GetString();
            return (decimal.TryParse(value, CultureInfo.InvariantCulture, out var amount) ? amount : null, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException)
        {
            diagnosticLog.Write($"NanoGPT balance request failed: {exception.Message}"); return (null, "!");
        }
    }
}
