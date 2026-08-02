namespace AIUsageMonitor.Core;

internal sealed record AppSettings
{
    internal const int DefaultPollIntervalMilliseconds = 15 * 60 * 1000;
    internal static readonly IReadOnlySet<int> AllowedPollIntervals = new HashSet<int>
    {
        60 * 1000,
        5 * 60 * 1000,
        DefaultPollIntervalMilliseconds,
        60 * 60 * 1000
    };

    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public int PollIntervalMilliseconds { get; init; } = DefaultPollIntervalMilliseconds;
    public string? Language { get; init; }
    public long? LastUpdateCheckUnix { get; init; }
    public bool WidgetVisible { get; init; } = true;
    public bool ShowClaudeCode { get; init; } = true;
    public bool ShowCodex { get; init; }
    public bool AlwaysOnTop { get; init; }
    public string? Theme { get; init; }
    public string? NtfyTopic { get; init; }
    public string? OpenRouterApiKeyProtected { get; init; }
    public string? NanoGptApiKeyProtected { get; init; }
    public bool[] ArmedLimits { get; init; } = new bool[3];

    internal AppSettings Normalize()
    {
        // v2.0.0-alpha.4 had a now-obsolete Codex 5h slot at index 2.
        var armed = ArmedLimits.Length switch
        {
            3 => ArmedLimits,
            4 => [ArmedLimits[0], ArmedLimits[1], ArmedLimits[3]],
            _ => new bool[3]
        };
        return this with
        {
            PollIntervalMilliseconds = AllowedPollIntervals.Contains(PollIntervalMilliseconds)
                ? PollIntervalMilliseconds
                : DefaultPollIntervalMilliseconds,
            ShowClaudeCode = ShowClaudeCode || !ShowCodex,
            Theme = Theme is "light" or "dark" ? Theme : null,
            NtfyTopic = string.IsNullOrWhiteSpace(NtfyTopic) ? null : NtfyTopic.Trim(),
            ArmedLimits = armed
        };
    }

    internal AppSettings WithArmedLimit(UsageLimit limit, bool armed)
    {
        var limits = (bool[])ArmedLimits.Clone();
        limits[(int)limit] = armed;
        return this with { ArmedLimits = limits };
    }

    internal string? OpenRouterApiKey => SecretProtector.Unprotect(OpenRouterApiKeyProtected);
    internal string? NanoGptApiKey => SecretProtector.Unprotect(NanoGptApiKeyProtected);
    internal AppSettings WithRouterKeys(string? openRouter, string? nanoGpt) => this with
    {
        OpenRouterApiKeyProtected = SecretProtector.Protect(openRouter),
        NanoGptApiKeyProtected = SecretProtector.Protect(nanoGpt)
    };
}
