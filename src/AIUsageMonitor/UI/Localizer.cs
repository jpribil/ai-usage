using System.Globalization;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

internal static class Localizer
{
    internal static bool IsCzech(AppSettings settings) => settings.Language switch
    {
        "cs" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("cs", StringComparison.OrdinalIgnoreCase)
    };

    internal static string Text(AppSettings settings, string key) => IsCzech(settings) ? Czech[key] : English[key];

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["refresh"] = "Refresh", ["frequency"] = "Update Frequency", ["appearance"] = "Appearance",
        ["system"] = "System Default", ["light"] = "Light", ["dark"] = "Dark", ["language"] = "Language",
        ["show"] = "Show Widget", ["topmost"] = "Always on Top", ["position"] = "Reset Position",
        ["autostart"] = "Start with Windows", ["channel"] = "Notification channel…", ["routerKeys"] = "Router API keys…",
        ["updates"] = "Check for Updates", ["updateToken"] = "GitHub update token…", ["exit"] = "Exit", ["minute1"] = "1 Minute", ["minute5"] = "5 Minutes",
        ["minute15"] = "15 Minutes", ["hour1"] = "1 Hour", ["now"] = "now", ["resets"] = "Resets",
        ["routers"] = "Routers", ["topicPrompt"] = "Enter your ntfy.sh channel (topic) name:", ["sendTest"] = "Send test", ["testSent"] = "Test sent.", ["testFailed"] = "Test failed:", ["openRouterPrompt"] = "Enter OpenRouter management API key:",
        ["nanoPrompt"] = "Enter nano-gpt.com API key:", ["githubPrompt"] = "Enter read-only GitHub token for update checks:"
    };

    private static readonly IReadOnlyDictionary<string, string> Czech = new Dictionary<string, string>
    {
        ["refresh"] = "Obnovit", ["frequency"] = "Frekvence aktualizace", ["appearance"] = "Vzhled",
        ["system"] = "Výchozí systémové", ["light"] = "Světlý", ["dark"] = "Tmavý", ["language"] = "Jazyk",
        ["show"] = "Zobrazit widget", ["topmost"] = "Vždy nahoře", ["position"] = "Obnovit pozici",
        ["autostart"] = "Spustit s Windows", ["channel"] = "Notifikační kanál…", ["routerKeys"] = "API klíče routerů…",
        ["updates"] = "Zkontrolovat aktualizace", ["updateToken"] = "GitHub token pro aktualizace…", ["exit"] = "Ukončit", ["minute1"] = "1 minuta", ["minute5"] = "5 minut",
        ["minute15"] = "15 minut", ["hour1"] = "1 hodina", ["now"] = "teď", ["resets"] = "Resety",
        ["routers"] = "Routery", ["topicPrompt"] = "Zadejte název kanálu (topic) pro ntfy.sh:", ["sendTest"] = "Odeslat test", ["testSent"] = "Testovací zpráva odeslána.", ["testFailed"] = "Test se nepodařilo odeslat:", ["openRouterPrompt"] = "Zadejte management API klíč OpenRouteru:",
        ["nanoPrompt"] = "Zadejte API klíč nano-gpt.com:", ["githubPrompt"] = "Zadejte GitHub token pouze pro čtení releasů:"
    };
}
