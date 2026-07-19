namespace OpenBlade.Tray.Tests;

public sealed class ShellThemeSelectionPolicyTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    public void ExplicitShellThemeSelectsContrastingGlyph(
        int systemUsesLightTheme,
        bool expected) =>
        Assert.Equal(
            expected,
            ShellThemeSelectionPolicy.ShouldUseLightTrayIconGlyphs(
                systemUsesLightTheme));

    [Fact]
    public void MissingShellThemeDefaultsToLightGlyph() =>
        Assert.True(
            ShellThemeSelectionPolicy.ShouldUseLightTrayIconGlyphs(null));
}
