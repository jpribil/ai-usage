namespace AIUsageMonitor.Core;

internal sealed record UsageSection(double Percentage, DateTimeOffset? ResetsAt, bool Available)
{
    internal static UsageSection Unavailable { get; } = new(0, null, false);
}

internal sealed record UsageData(
    UsageSection Session,
    UsageSection Weekly,
    uint? ResetCreditsAvailable = null);

internal sealed record AppUsageData(UsageData? ClaudeCode, UsageData? Codex)
{
    internal static AppUsageData Empty { get; } = new(null, null);
}

internal enum UsageLimit
{
    ClaudeSession = 0,
    ClaudeWeekly = 1,
    CodexSession = 2,
    CodexWeekly = 3
}

internal enum PollError
{
    AuthRequired,
    NoCredentials,
    TokenExpired,
    RequestFailed
}
