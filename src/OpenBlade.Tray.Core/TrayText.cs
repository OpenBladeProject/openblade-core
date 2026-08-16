using System.Globalization;
using System.Resources;

namespace OpenBlade.Tray;

internal static class TrayText
{
    private static readonly CultureInfo EnglishCulture =
        CultureInfo.GetCultureInfo("en-US");
    private static readonly ResourceManager ResourceManager = new(
        "OpenBlade.Tray.Core.Properties.Resources",
        typeof(TrayText).Assembly);

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return TryGet(key, CultureInfo.CurrentUICulture)
            ?? TryGet(key, EnglishCulture)
            ?? key;
    }

    public static string Format(string key, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(arguments);

        string localized = Get(key);
        string english = TryGet(key, EnglishCulture) ?? key;
        return FormatLocalized(localized, english, arguments);
    }

    internal static string FormatLocalized(
        string localized,
        string english,
        params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(localized);
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, localized, arguments);
        }
        catch (FormatException)
        {
            try
            {
                return string.Format(CultureInfo.CurrentCulture, english, arguments);
            }
            catch (FormatException)
            {
                return english;
            }
        }
    }

    private static string? TryGet(string key, CultureInfo culture)
    {
        try
        {
            string? value = ResourceManager.GetString(key, culture);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (MissingManifestResourceException)
        {
            return null;
        }
        catch (MissingSatelliteAssemblyException)
        {
            return null;
        }
    }
}
