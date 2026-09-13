using System.Globalization;

namespace OpenBlade.Tray;

// The linked notification coordinator needs only text lookup in these behavioral
// tests. Production resource loading is verified by the localization and UI lanes.
internal static class LocalizedResources
{
    internal static string Get(string key, string englishFallback) => englishFallback;

    internal static string Format(string key, string englishFallback, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, englishFallback, arguments);
}
