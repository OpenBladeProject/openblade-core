namespace OpenBlade.Tray.Tests;

public sealed class WhatsNewReleaseTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "OpenBlade.WhatsNew.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("0.45.0", "0.45.0")]
    [InlineData("0.45.0+first", "0.45.0+rebuilt")]
    [InlineData("0.45.0-nightly.106+first", "0.45.0-nightly.106+rebuilt")]
    [InlineData("0.45.0-nightly.106-attempt.2+first", "0.45.0-nightly.106-attempt.2+rebuilt")]
    public void SeenRelease_SuppressesAutomaticDisplayAcrossMetadataRebuilds(
        string seenProductVersion, string currentProductVersion)
    {
        var seen = new WhatsNewRelease(seenProductVersion, string.Empty);
        var current = new WhatsNewRelease(currentProductVersion, string.Empty);

        Assert.False(current.ShouldShow(seen.Version));
    }

    [Theory]
    [InlineData("0.44.0", "0.45.0")]
    [InlineData("0.45.0-nightly.105", "0.45.0-nightly.106")]
    [InlineData("0.45.0-nightly.106-attempt.1", "0.45.0-nightly.106-attempt.2")]
    [InlineData("0.45.0-nightly.106", "0.45.0")]
    public void DifferentReleaseNightlyOrAttempt_IsPending(string seenVersion, string currentVersion)
    {
        var release = new WhatsNewRelease(currentVersion, string.Empty);

        Assert.True(release.ShouldShow(seenVersion));
    }

    [Fact]
    public async Task ExistingPreferencesWithoutMarker_LeaveReleasePending()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "{\"theme\":\"Dark\",\"startMinimized\":true}");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();
        var release = new WhatsNewRelease("0.45.0", string.Empty);

        Assert.Null(preferences.LastSeenWhatsNewVersion);
        Assert.Null(UiPreferences.Default.LastSeenWhatsNewVersion);
        Assert.True(release.ShouldShow(preferences.LastSeenWhatsNewVersion));
        Assert.Equal(ThemePreference.Dark, preferences.Theme);
        Assert.True(preferences.StartMinimized);
    }

    [Fact]
    public async Task SeenMarker_RoundTripsAlongsideOtherPreferences()
    {
        string path = Path.Combine(directory, "preferences.json");
        var release = new WhatsNewRelease("0.45.0-nightly.106-attempt.2+commit", string.Empty);
        UiPreferences preferences = UiPreferences.Default with
        {
            LastSeenWhatsNewVersion = release.Version,
            Theme = ThemePreference.Dark,
            StartMinimized = true,
            TelemetryEnabled = true,
            UpdateChannel = UpdateChannel.Nightly,
        };

        await new UiPreferenceStore(path).SaveAsync(preferences);
        UiPreferences reloaded = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(release.Version, reloaded.LastSeenWhatsNewVersion);
        Assert.False(release.ShouldShow(reloaded.LastSeenWhatsNewVersion));
        Assert.Equal(preferences.Theme, reloaded.Theme);
        Assert.Equal(preferences.StartMinimized, reloaded.StartMinimized);
        Assert.Equal(preferences.TelemetryEnabled, reloaded.TelemetryEnabled);
        Assert.Equal(preferences.UpdateChannel, reloaded.UpdateChannel);
    }

    [Fact]
    public async Task MarkingOnePreferenceStoreSeen_LeavesAnotherStorePending()
    {
        var first = new UiPreferenceStore(Path.Combine(directory, "first.json"));
        var second = new UiPreferenceStore(Path.Combine(directory, "second.json"));
        var release = new WhatsNewRelease("0.45.0", string.Empty);
        await second.SaveAsync(UiPreferences.Default with { Theme = ThemePreference.Light });

        await first.SaveAsync(UiPreferences.Default with { LastSeenWhatsNewVersion = release.Version });

        Assert.False(release.ShouldShow((await first.LoadAsync()).LastSeenWhatsNewVersion));
        UiPreferences other = await second.LoadAsync();
        Assert.True(release.ShouldShow(other.LastSeenWhatsNewVersion));
        Assert.Equal(ThemePreference.Light, other.Theme);
    }

    [Fact]
    public void ExactReleaseHeading_TakesPrecedenceOverUnreleasedAndOlderNotes()
    {
        const string changelog = """
            # Changelog
            ## [Unreleased]
            ### Added
            - Future feature.
            ## [0.45.0] - 2026-10-30
            ### Changed
            - This release.
            ## [0.44.0] - 2026-10-29
            ### Fixed
            - Old fix.
            """;

        var release = new WhatsNewRelease("0.45.0+commit", changelog);

        WhatsNewSection section = Assert.Single(release.Sections);
        Assert.Equal("Changed", section.Heading);
        Assert.Equal("This release.", Assert.Single(section.Items));
    }

    [Fact]
    public void MissingExactRelease_UsesOnlyUnreleased()
    {
        const string changelog = """
            ## [Unreleased]
            ### Added
            - Current development feature.
            ## [0.45.0] - 2026-10-30
            ### Fixed
            - Published fix.
            """;

        var release = new WhatsNewRelease("0.45.0-nightly.106", changelog);

        WhatsNewSection section = Assert.Single(release.Sections);
        Assert.Equal("Added", section.Heading);
        Assert.Equal("Current development feature.", Assert.Single(section.Items));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Changelog\n")]
    [InlineData("## [0.44.0] - 2026-10-29\n### Fixed\n- Old fix.")]
    [InlineData("## [Unreleased]\n\n## [0.44.0] - 2026-10-29\n### Fixed\n- Old fix.")]
    [InlineData("## [0.45.0]\n\n## [0.44.0] - 2026-10-29\n### Fixed\n- Old fix.")]
    public void EmptyOrMissingApplicableNotes_ReturnNoSections(string changelog)
    {
        var release = new WhatsNewRelease("0.45.0", changelog);

        Assert.Empty(release.Sections);
    }

    [Fact]
    public void ReleaseNotes_ExposePlainTextAndJoinContinuationLines()
    {
        const string changelog = """
            ## [Unreleased]
            ### Added
            - Show **clear** `status` with [details](https://example.com/details).
              Continue on the next line. ([#859](https://github.com/OpenBladeProject/openblade-core/pull/859))
            - Another change. ([#860](https://github.com/OpenBladeProject/openblade-core/pull/860))
            ### Fixed
            - Preserve settings.
            """;

        var release = new WhatsNewRelease("0.45.0", changelog);

        Assert.Equal(2, release.Sections.Count);
        Assert.Equal("Added", release.Sections[0].Heading);
        Assert.Equal(
            ["Show clear status with details. Continue on the next line.", "Another change."],
            release.Sections[0].Items);
        Assert.Equal("Fixed", release.Sections[1].Heading);
        Assert.Equal("Preserve settings.", Assert.Single(release.Sections[1].Items));
    }

    [Fact]
    public void BundledChangelog_ContainsReadableReleaseNotes()
    {
        WhatsNewRelease release = WhatsNewRelease.Load("9999.0.0");

        Assert.NotEmpty(release.Sections);
        Assert.All(release.Sections, section =>
        {
            Assert.NotEmpty(section.Items);
            Assert.All(section.Items, item => Assert.False(string.IsNullOrWhiteSpace(item)));
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
