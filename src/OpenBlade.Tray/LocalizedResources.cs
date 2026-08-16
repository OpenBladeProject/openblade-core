using System.Globalization;
using System.Text;
using Microsoft.Windows.ApplicationModel.Resources;

namespace OpenBlade.Tray;

internal static class LocalizedResources
{
    private static readonly ResourceLoader Loader = new();
    private static readonly ResourceManager Manager = new();
    private static readonly ResourceMap ResourceMap =
        Manager.MainResourceMap.GetSubtree("Resources");
    private static ResourceContext? overrideContext;

    internal static void SetLanguageOverride(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            overrideContext = null;
            return;
        }

        ResourceContext context = Manager.CreateResourceContext();
        context.QualifierValues[KnownResourceQualifierName.Language] = language;
        overrideContext = context;
    }

    internal static string Get(string key, string englishFallback)
    {
        try
        {
            string localized = overrideContext is { } context
                ? ResourceMap.TryGetValue(ToResourceMapPath(key), context)?.ValueAsString
                    ?? string.Empty
                : Loader.GetString(key);
            return string.IsNullOrWhiteSpace(localized) ? englishFallback : localized;
        }
        catch
        {
            return englishFallback;
        }
    }

    internal static string? GetResolvedLanguage(string key)
    {
        try
        {
            ResourceCandidate? candidate = overrideContext is { } context
                ? ResourceMap.TryGetValue(ToResourceMapPath(key), context)
                : ResourceMap.TryGetValue(ToResourceMapPath(key));
            return candidate is not null
                && candidate.QualifierValues.TryGetValue(
                    KnownResourceQualifierName.Language,
                    out string? language)
                    ? language
                    : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string Format(
        string key,
        string englishFallback,
        params object?[] arguments)
    {
        string localized = Get(key, englishFallback);
        try
        {
            return string.Format(CultureInfo.CurrentUICulture, localized, arguments);
        }
        catch (FormatException) when (!string.Equals(
            localized,
            englishFallback,
            StringComparison.Ordinal))
        {
            try
            {
                return string.Format(
                    CultureInfo.GetCultureInfo("en-US"),
                    englishFallback,
                    arguments);
            }
            catch (FormatException)
            {
                return englishFallback;
            }
        }
        catch (FormatException)
        {
            return englishFallback;
        }
    }

    private static string ToResourceMapPath(string key)
    {
        if (!key.Contains('.', StringComparison.Ordinal))
        {
            return key;
        }

        var path = new StringBuilder(key.Length);
        bool insideNamespace = false;
        foreach (char character in key)
        {
            insideNamespace = character switch
            {
                '[' => true,
                ']' => false,
                _ => insideNamespace,
            };
            path.Append(character == '.' && !insideNamespace ? '/' : character);
        }

        return path.ToString();
    }

}
