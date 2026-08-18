using System.Globalization;
using System.Resources;

namespace OpenBlade.Setup;

internal static class SetupStrings
{
    private static readonly ResourceManager Resources = new(
        "OpenBlade.Setup.Properties.Resources",
        typeof(SetupStrings).Assembly);

    internal static string Get(string english)
    {
        if (string.IsNullOrEmpty(english))
        {
            return english;
        }

        try
        {
            string? localized = Resources.GetString(english, CultureInfo.CurrentUICulture);
            return string.IsNullOrWhiteSpace(localized) ? english : localized;
        }
        catch (MissingManifestResourceException)
        {
            return english;
        }
        catch (MissingSatelliteAssemblyException)
        {
            return english;
        }
        catch (InvalidOperationException)
        {
            return english;
        }
    }

    internal static string Format(string englishFormat, params object[] arguments)
    {
        return FormatLocalized(englishFormat, Get(englishFormat), arguments);
    }

    internal static string FormatLocalized(
        string englishFormat,
        string localizedFormat,
        params object[] arguments)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, localizedFormat, arguments);
        }
        catch (FormatException) when (!string.Equals(
            localizedFormat,
            englishFormat,
            StringComparison.Ordinal))
        {
            return string.Format(CultureInfo.CurrentCulture, englishFormat, arguments);
        }
    }
}
