using Microsoft.Windows.ApplicationModel.Resources;

namespace OpenBlade.Tray;

internal static class LocalizedResources
{
    private static readonly ResourceLoader Loader = new();

    internal static string Get(string key, string englishFallback)
    {
        try
        {
            string localized = Loader.GetString(key);
            return string.IsNullOrWhiteSpace(localized) ? englishFallback : localized;
        }
        catch
        {
            return englishFallback;
        }
    }
}
