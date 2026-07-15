namespace OpenBlade.Tray.Tests;

public sealed class ThemeSelectionPolicyTests
{
    [Theory]
    [InlineData(ThemePreference.FollowWindows, ResolvedTheme.Default)]
    [InlineData(ThemePreference.Light, ResolvedTheme.Light)]
    [InlineData(ThemePreference.Dark, ResolvedTheme.Dark)]
    public void Resolve_MapsEveryPersistedPreference(
        ThemePreference preference,
        ResolvedTheme expected) =>
        Assert.Equal(expected, ThemeSelectionPolicy.Resolve(preference));

    [Fact]
    public void Resolve_RejectsUnknownPreference() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThemeSelectionPolicy.Resolve((ThemePreference)int.MaxValue));
}
