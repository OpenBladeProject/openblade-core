using System.Collections.Frozen;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Resources;

namespace OpenBlade.Tray;

// Resolve each property through the shared resource map. WinUI's x:Uid lookup
// creates a native MRM subtree for each element that survives view retirement.
internal sealed partial class LocalizedXamlResourceLoader : CustomXamlResourceLoader
{
    private static readonly Lazy<FrozenDictionary<string, string>> EnglishFallbacks =
        new(ReadEnglishFallbacks);

    protected override object GetResource(
        string resourceId,
        string objectType,
        string propertyName,
        string propertyType) =>
        GetText(resourceId);

    internal static string GetText(string resourceId) =>
        LocalizedResources.Get(resourceId, EnglishFallbacks.Value[resourceId]);

    private static FrozenDictionary<string, string> ReadEnglishFallbacks()
    {
        // Embed the authoritative catalog itself so fallback strings stay in one
        // source and remain available even when the PRI lookup fails.
        using Stream stream = typeof(LocalizedXamlResourceLoader).Assembly
            .GetManifestResourceStream("OpenBlade.Tray.LocalizationFallbacks.resw")
            ?? throw new InvalidOperationException("The English XAML fallback catalog is missing.");
        XDocument document = XDocument.Load(stream);
        return (document.Root?.Elements("data")
                ?? throw new InvalidOperationException("The English XAML fallback catalog is empty."))
            .ToFrozenDictionary(
                entry => (string?)entry.Attribute("name")
                    ?? throw new InvalidOperationException("A XAML fallback resource has no name."),
                entry => (string?)entry.Element("value")
                    ?? throw new InvalidOperationException("A XAML fallback resource has no value."),
                StringComparer.Ordinal);
    }
}
