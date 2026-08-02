using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core;
using AIUsageMonitor.Infrastructure;

namespace AIUsageMonitor.Services;

internal abstract record CredentialSource
{
    internal sealed record WindowsFile(string Path) : CredentialSource;
    internal sealed record WslDistro(string Distro) : CredentialSource;
}

internal sealed record ClaudeCredential(string AccessToken, long? ExpiresAtMilliseconds, CredentialSource Source)
{
    internal bool IsExpired => ExpiresAtMilliseconds is long expires && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expires;
}

internal sealed record CodexCredential(string AccessToken, string? AccountId, string Path);

internal sealed class CredentialStore(DiagnosticLog diagnosticLog)
{
    private const string ClaudeRelativePath = ".claude/.credentials.json";

    internal async Task<IReadOnlyList<ClaudeCredential>> FindClaudeCredentialsAsync(CancellationToken cancellationToken)
    {
        var credentials = new List<ClaudeCredential>();
        var windowsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        var fromWindows = TryParseClaudeCredential(ReadFileIfPresent(windowsPath), new CredentialSource.WindowsFile(windowsPath));
        if (fromWindows is not null)
        {
            credentials.Add(fromWindows);
        }

        foreach (var distro in await ListWslDistrosAsync(cancellationToken))
        {
            var bytes = await RunAndCaptureAsync("wsl.exe", ["-d", distro, "--", "sh", "-lc", $"cat ~/{ClaudeRelativePath}"], TimeSpan.FromSeconds(5), cancellationToken);
            var credential = TryParseClaudeCredential(bytes is null ? null : DecodeConsoleBytes(bytes), new CredentialSource.WslDistro(distro));
            if (credential is not null)
            {
                credentials.Add(credential);
            }
        }

        return credentials;
    }

    internal CodexCredential? FindCodexCredential()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var path = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json")
            : Path.Combine(codexHome, "auth.json");
        var json = ReadFileIfPresent(path);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tokens", out var tokens) ||
                !tokens.TryGetProperty("access_token", out var accessToken) ||
                accessToken.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(accessToken.GetString()))
            {
                return null;
            }

            var accountId = tokens.TryGetProperty("account_id", out var account) && account.ValueKind == JsonValueKind.String
                ? account.GetString()
                : null;
            return new CodexCredential(accessToken.GetString()!, accountId, path);
        }
        catch (JsonException exception)
        {
            diagnosticLog.Write($"Unable to parse Codex credential metadata: {exception.Message}");
            return null;
        }
    }

    internal async Task<bool> RefreshClaudeAsync(CredentialSource source, CancellationToken cancellationToken)
    {
        if (source is CredentialSource.WslDistro wsl)
        {
            return await RunSilentlyAsync("wsl.exe", ["-d", wsl.Distro, "--", "bash", "-lic", "claude -p ."], TimeSpan.FromSeconds(30), cancellationToken);
        }

        return await RunSilentlyAsync(ClaudeExecutable(), ["-p", "."], TimeSpan.FromSeconds(30), cancellationToken, sanitizeClaudeEnvironment: true);
    }

    internal Task<bool> RefreshCodexAsync(CancellationToken cancellationToken) =>
        RunSilentlyAsync("codex.cmd", ["exec", "."], TimeSpan.FromSeconds(30), cancellationToken);

    internal IReadOnlyList<string> ClaudeWatchPaths() =>
    [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json")];

    internal string CodexWatchPath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json")
            : Path.Combine(codexHome, "auth.json");
    }

    private async Task<IReadOnlyList<string>> ListWslDistrosAsync(CancellationToken cancellationToken)
    {
        var bytes = await RunAndCaptureAsync("wsl.exe", ["-l", "-q"], TimeSpan.FromSeconds(5), cancellationToken);
        return bytes is null
            ? []
            : DecodeConsoleBytes(bytes).Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static ClaudeCredential? TryParseClaudeCredential(string? json, CredentialSource source)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var accessToken) ||
                accessToken.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var expiresAt = oauth.TryGetProperty("expiresAt", out var expires) && expires.TryGetInt64(out var milliseconds)
                ? (long?)milliseconds
                : null;
            return new ClaudeCredential(accessToken.GetString() ?? string.Empty, expiresAt, source);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadFileIfPresent(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ClaudeExecutable()
    {
        var standardPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(standardPath) ? standardPath : "claude.exe";
    }

    private static string DecodeConsoleBytes(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        var sampleLength = Math.Min(bytes.Length, 128);
        var zeroes = 0;
        for (var index = 1; index < sampleLength; index += 2)
        {
            if (bytes[index] == 0)
            {
                zeroes++;
            }
        }

        return sampleLength > 8 && zeroes >= sampleLength / 4
            ? Encoding.Unicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]?> RunAndCaptureAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = StartProcess(executable, arguments, redirectOutput: true, sanitizeClaudeEnvironment: false);
            var readTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, cancellationToken);
            if (!await WaitForExitAsync(process, timeout, cancellationToken))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return await readTask;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<bool> RunSilentlyAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, bool sanitizeClaudeEnvironment = false)
    {
        try
        {
            using var process = StartProcess(executable, arguments, redirectOutput: false, sanitizeClaudeEnvironment);
            if (await WaitForExitAsync(process, timeout, cancellationToken))
            {
                return process.ExitCode == 0;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }

        return false;
    }

    private static Process StartProcess(string executable, IReadOnlyList<string> arguments, bool redirectOutput, bool sanitizeClaudeEnvironment)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (sanitizeClaudeEnvironment)
        {
            start.Environment.Remove("CLAUDECODE");
            start.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
        }

        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start CLI process.");
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }
}
