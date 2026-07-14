using Microsoft.Win32;

namespace OpenSynapse.Tray.Tests;

public sealed class WindowsThemeChangePolicyTests
{
    [Theory]
    [InlineData(UserPreferenceCategory.General)]
    [InlineData(UserPreferenceCategory.VisualStyle)]
    [InlineData(UserPreferenceCategory.Window)]
    public void ShouldReapply_TracksRelevantWindowsChangesOnlyWhenFollowingWindows(UserPreferenceCategory category)
    {
        Assert.True(WindowsThemeChangePolicy.ShouldReapply(ThemePreference.FollowWindows, category));
        Assert.False(WindowsThemeChangePolicy.ShouldReapply(ThemePreference.Light, category));
        Assert.False(WindowsThemeChangePolicy.ShouldReapply(ThemePreference.Dark, category));
    }

    [Fact]
    public void ShouldReapply_IgnoresUnrelatedPreferenceChanges()
    {
        Assert.False(WindowsThemeChangePolicy.ShouldReapply(
            ThemePreference.FollowWindows,
            UserPreferenceCategory.Keyboard));
    }
}
