using System.Security.Cryptography;
using System.Text;

namespace AIUsageMonitor.Core;

internal static class SecretProtector
{
    internal static string? Protect(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));

    internal static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}
