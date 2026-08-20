using System.Globalization;
using System.Resources;

namespace OpenBlade.Tray;

public static class TrayLocalization
{
    private static CultureInfo? resourceCultureOverride;

    internal static CultureInfo ResourceCulture =>
        Volatile.Read(ref resourceCultureOverride) ?? CultureInfo.CurrentUICulture;

    public static void SetLanguageOverride(string? language)
    {
        CultureInfo? culture = string.IsNullOrWhiteSpace(language)
            ? null
            : CultureInfo.GetCultureInfo(language);
        Volatile.Write(ref resourceCultureOverride, culture);
    }
}

internal static class TrayText
{
    private static readonly CultureInfo EnglishCulture =
        CultureInfo.GetCultureInfo("en-US");
    private static readonly ResourceManager ResourceManager = new(
        "OpenBlade.Tray.Core.Properties.Resources",
        typeof(TrayText).Assembly);

    public static string Get(string key) => Get(key, key);

    public static string Get(string key, string english)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(english);
        return TryGet(key, TrayLocalization.ResourceCulture)
            ?? TryGet(key, EnglishCulture)
            ?? english;
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
