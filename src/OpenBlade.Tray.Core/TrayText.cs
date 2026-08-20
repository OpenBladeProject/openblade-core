using System.Globalization;
using System.Resources;

namespace OpenBlade.Tray;

public static class TrayLocalization
{
    private static readonly AsyncLocal<TemporaryLanguageOverride?> TemporaryOverride =
        new();
    private static CultureInfo? resourceCultureOverride;

    public static string CurrentLanguageName => ResourceCulture.Name;

    internal static CultureInfo ResourceCulture
    {
        get
        {
            for (TemporaryLanguageOverride? current = TemporaryOverride.Value;
                current is not null;
                current = current.Previous)
            {
                if (current.IsActive)
                {
                    return current.Culture;
                }
            }

            return Volatile.Read(ref resourceCultureOverride)
                ?? CultureInfo.CurrentUICulture;
        }
    }

    public static void SetLanguageOverride(string? language)
    {
        CultureInfo? culture = string.IsNullOrWhiteSpace(language)
            ? null
            : CultureInfo.GetCultureInfo(language);
        Volatile.Write(ref resourceCultureOverride, culture);
    }

    public static IDisposable BeginTemporaryLanguageOverride(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var temporary = new TemporaryLanguageOverride(
            CultureInfo.GetCultureInfo(language),
            TemporaryOverride.Value);
        TemporaryOverride.Value = temporary;
        return temporary;
    }

    private sealed class TemporaryLanguageOverride(
        CultureInfo culture,
        TemporaryLanguageOverride? previous) : IDisposable
    {
        private int active = 1;

        internal CultureInfo Culture { get; } = culture;

        internal TemporaryLanguageOverride? Previous { get; } = previous;

        internal bool IsActive => Volatile.Read(ref active) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref active, 0) == 0)
            {
                return;
            }

            if (ReferenceEquals(TemporaryOverride.Value, this))
            {
                TemporaryOverride.Value = Previous;
            }
        }
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
