namespace OpenBlade.Tray.Tests;

public sealed class AppearancePreferenceTests
{
    [Fact]
    public void NavigationNormalizationPreservesValidOrderAndRestoresMissingPages()
    {
        IReadOnlyList<PrimaryNavigationItemKind> result =
            PrimaryNavigationPreferenceDefaults.Normalize(
            [
                PrimaryNavigationItemKind.Settings,
                PrimaryNavigationItemKind.Laptop,
                PrimaryNavigationItemKind.Settings,
            ]);

        Assert.Equal(
            [
                PrimaryNavigationItemKind.Settings,
                PrimaryNavigationItemKind.Laptop,
                PrimaryNavigationItemKind.Profiles,
                PrimaryNavigationItemKind.Lighting,
                PrimaryNavigationItemKind.Display,
            ],
            result);
    }

    [Theory]
    [InlineData("#47e10c", "#47E10C")]
    [InlineData("123abc", "#123ABC")]
    [InlineData("  #ABCDEF  ", "#ABCDEF")]
    public void CustomAccentNormalizationAcceptsRgbHex(string value, string expected)
    {
        Assert.True(AppAccentColorPreferenceDefaults.TryNormalizeHexColor(
            value,
            out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#12345678")]
    [InlineData("#GHIJKL")]
    public void CustomAccentNormalizationRejectsInvalidValues(string value)
    {
        Assert.False(AppAccentColorPreferenceDefaults.TryNormalizeHexColor(
            value,
            out _));
    }
}
