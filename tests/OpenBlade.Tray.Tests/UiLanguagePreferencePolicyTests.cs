namespace OpenBlade.Tray.Tests;

public sealed class UiLanguagePreferencePolicyTests
{
    [Theory]
    [InlineData(UiLanguagePreference.FollowSystem, null)]
    [InlineData(UiLanguagePreference.English, "en-US")]
    [InlineData(UiLanguagePreference.SimplifiedChinese, "zh-CN")]
    [InlineData(UiLanguagePreference.Russian, "ru-RU")]
    public void ResolveCultureName_MapsEverySupportedPreference(
        UiLanguagePreference preference,
        string? expected) =>
        Assert.Equal(expected, UiLanguagePreferencePolicy.ResolveCultureName(preference));

    [Fact]
    public void ResolveCultureName_RejectsUnknownPreference() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UiLanguagePreferencePolicy.ResolveCultureName((UiLanguagePreference)int.MaxValue));

    [Fact]
    public void Normalize_UsesFollowSystemForUnknownPreference() =>
        Assert.Equal(
            UiLanguagePreference.FollowSystem,
            UiLanguagePreferencePolicy.Normalize((UiLanguagePreference)int.MaxValue));
}
