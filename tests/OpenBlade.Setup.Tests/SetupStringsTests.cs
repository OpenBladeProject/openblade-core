using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupStringsTests
{
    [Fact]
    public void Get_MissingCultureFallsBackToAuthoritativeEnglish()
    {
        using CultureScope culture = new("fr-FR");

        Assert.Equal(
            "Uninstall OpenBlade",
            SetupStrings.Get("Uninstall OpenBlade"));
        Assert.Equal(
            "Missing test resource",
            SetupStrings.Get("Missing test resource"));
    }

    [Fact]
    public void Get_PseudoCultureUsesExpandedResource()
    {
        using CultureScope culture = new("qps-ploc");

        string localized = SetupStrings.Get("Uninstall OpenBlade");

        Assert.StartsWith("[!!", localized, StringComparison.Ordinal);
        Assert.EndsWith("!!]", localized, StringComparison.Ordinal);
        Assert.NotEqual("Uninstall OpenBlade", localized);
    }

    [Fact]
    public void Format_PreservesInvariantDiagnosticArgument()
    {
        using CultureScope culture = new("qps-ploc");

        string localized = SetupStrings.Format(
            "OpenBlade could not be removed.\n\n{0}",
            "0x80070005: access denied");

        Assert.Contains("0x80070005: access denied", localized, StringComparison.Ordinal);
        Assert.StartsWith("[!!", localized, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_MalformedTranslationFallsBackToEnglishFormat()
    {
        string formatted = SetupStrings.FormatLocalized(
            "OpenBlade could not be removed.\n\n{0}",
            "Malformed translation {1}",
            "access denied");

        Assert.Equal("OpenBlade could not be removed.\n\naccess denied", formatted);
    }
}
