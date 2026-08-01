using System.Reflection;

namespace AIUsageMonitor.UI;

internal static class RouterIcons
{
    internal static Image OpenRouter => OpenRouterImage.Value;
    internal static Image NanoGpt => NanoGptImage.Value;

    private static readonly Lazy<Image> OpenRouterImage = new(() => Load("ico.or.png"));
    private static readonly Lazy<Image> NanoGptImage = new(() => Load("ico.ng.png"));

    private static Image Load(string name)
    {
        var resourceName = $"AIUsageMonitor.Resources.RouterIcons.{name}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded router icon: {resourceName}");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }
}
