using System.Text;
using AIUsageMonitor.Core;
using AIUsageMonitor.Infrastructure;

namespace AIUsageMonitor.Services;

internal sealed record NtfySendResult(bool Succeeded, string? Error);

internal sealed class NtfyNotifier(HttpClient httpClient, DiagnosticLog diagnosticLog)
{
    internal Task<NtfySendResult> SendResetNotificationAsync(string? topic, UsageLimit limit, CancellationToken cancellationToken)
    {
        var message = limit switch
        {
            UsageLimit.ClaudeSession => "Claude 5h limit reset",
            UsageLimit.ClaudeWeekly => "Claude 7d limit reset",
            UsageLimit.CodexWeekly => "ChatGPT 7d limit reset",
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
        return SendAsync(topic, message, cancellationToken);
    }

    internal Task<NtfySendResult> SendTestNotificationAsync(string? topic, CancellationToken cancellationToken) =>
        SendAsync(topic, "Test notification from AI Usage Monitor.", cancellationToken);

    private async Task<NtfySendResult> SendAsync(string? topic, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return new NtfySendResult(false, "Název kanálu nesmí být prázdný.");
        }

        try
        {
            var encodedTopic = Uri.EscapeDataString(topic.Trim());
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://ntfy.sh/{encodedTopic}")
            {
                Content = new StringContent(message, Encoding.UTF8, "text/plain")
            };
            request.Headers.Add("Title", AppMetadata.ProductName);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = $"Server ntfy vrátil HTTP {(int)response.StatusCode}.";
                diagnosticLog.Write($"ntfy notification was rejected with status {(int)response.StatusCode}.");
                return new NtfySendResult(false, error);
            }

            return new NtfySendResult(true, null);
        }
        catch (HttpRequestException exception)
        {
            diagnosticLog.Write($"Unable to send ntfy notification: {exception.Message}");
            return new NtfySendResult(false, exception.Message);
        }
        catch (OperationCanceledException)
        {
            const string error = "Vypršel časový limit při spojení s ntfy.";
            diagnosticLog.Write(error);
            return new NtfySendResult(false, error);
        }
    }
}
