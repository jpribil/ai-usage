using System.Text;
using AIUsageMonitor.Core;
using AIUsageMonitor.Infrastructure;

namespace AIUsageMonitor.Services;

internal sealed class NtfyNotifier(HttpClient httpClient, DiagnosticLog diagnosticLog)
{
    internal async Task SendResetNotificationAsync(string? topic, UsageLimit limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return;
        }

        var message = limit switch
        {
            UsageLimit.ClaudeSession => "Claude 5h limit reset",
            UsageLimit.ClaudeWeekly => "Claude 7d limit reset",
            UsageLimit.CodexSession => "ChatGPT 5h limit reset",
            UsageLimit.CodexWeekly => "ChatGPT 7d limit reset",
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
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
                diagnosticLog.Write($"ntfy notification was rejected with status {(int)response.StatusCode}.");
            }
        }
        catch (HttpRequestException exception)
        {
            diagnosticLog.Write($"Unable to send ntfy notification: {exception.Message}");
        }
    }
}
