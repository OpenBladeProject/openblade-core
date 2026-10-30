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

        string? marker = await new WhatsNewSeenStore(Path.Combine(directory, "whats-new.version")).LoadAsync();
        Assert.Null(marker);
        Assert.True(release.ShouldShow(marker));
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
            Theme = ThemePreference.Dark,
            StartMinimized = true,
            TelemetryEnabled = true,
            UpdateChannel = UpdateChannel.Nightly,
        };
        var preferencesStore = new UiPreferenceStore(path);
        await preferencesStore.SaveAsync(preferences);
        byte[] original = await File.ReadAllBytesAsync(path);
        var seen = new WhatsNewSeenStore(Path.Combine(directory, "whats-new.version"));

        await seen.SaveAsync(release.Version);
        string? reloaded = await seen.LoadAsync();

        Assert.Equal(release.Version, reloaded);
        Assert.False(release.ShouldShow(reloaded));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MarkingOnePreferenceStoreSeen_LeavesAnotherStorePending()
    {
        var first = new WhatsNewSeenStore(Path.Combine(directory, "first", "whats-new.version"));
        var second = new WhatsNewSeenStore(Path.Combine(directory, "second", "whats-new.version"));
        var release = new WhatsNewRelease("0.45.0", string.Empty);

        await first.SaveAsync(release.Version);

        Assert.False(release.ShouldShow(await first.LoadAsync()));
        Assert.True(release.ShouldShow(await second.LoadAsync()));
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

    [Fact]
    public void ContinuationParagraphsNestedItemsAndCodeRemainInTheirBullet()
    {
        const string changelog = """
            ## [Unreleased]
            ### Added
            - Parent
            lazy continuation.

            Second paragraph.
              - Child.
              ```powershell
              echo test
              ```
            - Next.
            """;
        var release = new WhatsNewRelease("0.45.0", changelog);

        Assert.Equal(
            ["Parent lazy continuation.\n\nSecond paragraph.\n• Child.\n  echo test", "Next."],
            Assert.Single(release.Sections).Items);
    }

    [Fact]
    public void CombinedPullRequestReferencesAreOmittedFromSummary()
    {
        const string changelog = """
            ## [Unreleased]
            ### Fixed
            - Fix setup. ([#819](https://github.com/a/b/pull/819), [catalog #27](https://github.com/a/c/pull/27))
            """;
        var release = new WhatsNewRelease("0.45.0", changelog);

        Assert.Equal("Fix setup.", Assert.Single(Assert.Single(release.Sections).Items));
    }

    [Fact]
    public async Task AcknowledgementDoesNotReadOrOverwriteLockedOrInvalidUserSettings()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "{invalid json}");
        var seen = new WhatsNewSeenStore(Path.Combine(directory, "whats-new.version"));
        await using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await seen.SaveAsync("0.45.0");
        }
        Assert.Equal("{invalid json}", await File.ReadAllTextAsync(path));
        Assert.Equal("0.45.0", await seen.LoadAsync());
    }

    [Fact]
    public async Task AcknowledgementReplacementIsBoundedAndLeavesNoTemporaryFiles()
    {
        string path = Path.Combine(directory, "whats-new.version");
        var seen = new WhatsNewSeenStore(path);
        await seen.SaveAsync("0.44.0");
        await seen.SaveAsync("0.45.0");
        Assert.Equal("0.45.0", await seen.LoadAsync());
        Assert.Single(Directory.GetFiles(directory));
        await File.WriteAllTextAsync(path, new string('x', 257));
        Assert.Null(await seen.LoadAsync());
    }

    [Fact]
    public void ExactReleaseHeadingAllowsSurroundingWhitespace()
    {
        var release = new WhatsNewRelease("0.45.0",
            "## [Unreleased]\n### Added\n- Future.\n  ## [0.45.0]  \n### Fixed\n- Current.");
        Assert.Equal("Current.", Assert.Single(Assert.Single(release.Sections).Items));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
