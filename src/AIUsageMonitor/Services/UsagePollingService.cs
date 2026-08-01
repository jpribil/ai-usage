using AIUsageMonitor.Core;

namespace AIUsageMonitor.Services;

internal sealed record PollBatchResult(AppUsageData Data, PollError? ClaudeError, PollError? CodexError)
{
    internal bool HasSuccess => Data.ClaudeCode is not null || Data.Codex is not null;
}

internal sealed class UsagePollingService(IUsageProvider claude, IUsageProvider codex)
{
    internal async Task<PollBatchResult> PollAsync(bool pollClaude, bool pollCodex, CancellationToken cancellationToken)
    {
        var claudeTask = pollClaude ? claude.PollAsync(cancellationToken) : Task.FromResult(new ServicePollResult(null, null));
        var codexTask = pollCodex ? codex.PollAsync(cancellationToken) : Task.FromResult(new ServicePollResult(null, null));
        await Task.WhenAll(claudeTask, codexTask);
        var claudeResult = await claudeTask;
        var codexResult = await codexTask;
        return new PollBatchResult(new AppUsageData(claudeResult.Data, codexResult.Data), claudeResult.Error, codexResult.Error);
    }
}
