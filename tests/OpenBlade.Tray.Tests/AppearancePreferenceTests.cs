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
                PrimaryNavigationItemKind.Display,
                PrimaryNavigationItemKind.ThxAudio,
                PrimaryNavigationItemKind.Lighting,
            ],
            result);
    }

    [Fact]
    public void NavigationNormalizationMigratesTheFormerDefaultOrder()
    {
        IReadOnlyList<PrimaryNavigationItemKind> result =
            PrimaryNavigationPreferenceDefaults.Normalize(
            [
                PrimaryNavigationItemKind.Profiles,
                PrimaryNavigationItemKind.Laptop,
                PrimaryNavigationItemKind.Lighting,
                PrimaryNavigationItemKind.Display,
                PrimaryNavigationItemKind.Settings,
            ]);

        Assert.Equal(PrimaryNavigationPreferenceDefaults.Items, result);
    }

    [Fact]
    public void NavigationDefaultsPlaceThxAudioAfterLaptopBeforeLighting()
    {
        Assert.Equal(
            [
                PrimaryNavigationItemKind.Profiles,
                PrimaryNavigationItemKind.Display,
                PrimaryNavigationItemKind.Laptop,
                PrimaryNavigationItemKind.ThxAudio,
                PrimaryNavigationItemKind.Lighting,
                PrimaryNavigationItemKind.Settings,
            ],
            PrimaryNavigationPreferenceDefaults.Items);
        Assert.Equal(PrimaryNavigationPreferenceDefaults.Items,
            PrimaryNavigationPreferenceDefaults.Normalize(null));
        Assert.Equal(PrimaryNavigationPreferenceDefaults.Items,
            PrimaryNavigationPreferenceDefaults.Normalize(
                [
                    PrimaryNavigationItemKind.Profiles,
                    PrimaryNavigationItemKind.Display,
                    PrimaryNavigationItemKind.Laptop,
                    PrimaryNavigationItemKind.Lighting,
                    PrimaryNavigationItemKind.Settings,
                ]));
    }

    [Fact]
    public void NavigationAddsThxAudioWithoutMovingCustomizedExistingItems()
    {
        PrimaryNavigationItemKind[] customized =
        [
            PrimaryNavigationItemKind.Settings,
            PrimaryNavigationItemKind.Lighting,
            PrimaryNavigationItemKind.Profiles,
            PrimaryNavigationItemKind.Laptop,
            PrimaryNavigationItemKind.Display,
        ];
        IReadOnlyList<PrimaryNavigationItemKind> migrated = PrimaryNavigationPreferenceDefaults.Normalize(customized);
        Assert.Equal(customized, migrated.Take(customized.Length));
        Assert.Equal(PrimaryNavigationItemKind.ThxAudio, migrated[^1]);
        Assert.Equal(migrated, PrimaryNavigationPreferenceDefaults.Normalize(migrated));
    }

    [Fact]
    public void ExistingSerializedNavigationIntegersKeepTheirMeaning()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, new[]
        {
            (int)PrimaryNavigationItemKind.Profiles,
            (int)PrimaryNavigationItemKind.Laptop,
            (int)PrimaryNavigationItemKind.Lighting,
            (int)PrimaryNavigationItemKind.Display,
            (int)PrimaryNavigationItemKind.Settings,
            (int)PrimaryNavigationItemKind.ThxAudio,
        });
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
