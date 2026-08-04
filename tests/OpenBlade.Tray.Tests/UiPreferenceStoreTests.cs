using System.Collections;
using System.Text.Json;

namespace OpenBlade.Tray.Tests;

public sealed class UiPreferenceStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.Tray.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsFollowWindows()
    {
        UiPreferenceStore store = new(Path.Combine(directory, "preferences.json"));

        UiPreferences preferences = await store.LoadAsync();

        Assert.Equal(ThemePreference.FollowWindows, preferences.Theme);
        Assert.True(preferences.LiveReadingsEnabled);
        Assert.Equal(
            LiveReadingsCadence.EveryTwoSeconds,
            preferences.EffectiveLiveReadingsCadence);
        Assert.Equal(
            TrayHoverLabelMode.ProfileAndPowerFlow,
            preferences.TrayHoverLabelMode);
        Assert.False(preferences.StartMinimized);
        Assert.Equal(UpdateChannel.Stable, preferences.EffectiveUpdateChannel);
        Assert.Equal(UpdateCheckInterval.Daily, preferences.EffectiveUpdateCheckInterval);
        Assert.Equal(
            UpdateCheckInterval.ManualOnly,
            preferences.EffectiveFirmwareUpdateCheckInterval);
        Assert.False(preferences.UpdateCheckPromptShown);
        Assert.Null(preferences.LastSuccessfulUpdateCheckAt);
        Assert.Null(preferences.UpdateCheckRetryAfter);
        Assert.Null(preferences.UpdateCheckEntityTag);
        Assert.Equal(
            TelemetryPreferenceDefaults.DashboardItems,
            preferences.EffectiveDashboardTelemetryItems);
        Assert.Equal(
            TelemetryPreferenceDefaults.TrayHoverItems,
            preferences.EffectiveTrayHoverLabelItems);
        Assert.Equal(
            DashboardCardPreferenceDefaults.Cards,
            preferences.EffectiveDashboardCards);
        Assert.Equal(
            PrimaryNavigationPreferenceDefaults.Items,
            preferences.EffectivePrimaryNavigationItems);
        Assert.Equal(AppAccentColorMode.RazerGreen, preferences.EffectiveAccentColorMode);
        Assert.Equal(
            AppAccentColorPreferenceDefaults.RazerGreenHex,
            preferences.EffectiveCustomAccentColor);
        Assert.Equal(
            Enum.GetValues<SpecialKeySlot>(),
            preferences.EffectiveSpecialKeyBindings.Select(binding => binding.Slot));
        Assert.All(
            preferences.EffectiveSpecialKeyBindings,
            binding => Assert.Equal(SpecialKeyActionKind.Default, binding.Action.Kind));
    }

    [Fact]
    public async Task SaveAsync_PersistsThemeAsJson()
    {
        string path = Path.Combine(directory, "preferences.json");
        UiPreferenceStore store = new(path);

        await store.SaveAsync(new UiPreferences(
            ThemePreference.Dark,
            LiveReadingsEnabled: false,
            TrayHoverLabelMode: TrayHoverLabelMode.BatteryPowerFlow,
            LiveReadingsRefreshCadence: LiveReadingsCadence.EveryTenSeconds,
            StartMinimized: true,
            DashboardTelemetryItems:
            [
                LiveReadingItemKind.GpuTemperature,
                LiveReadingItemKind.BatteryPower,
            ],
            TrayHoverLabelItems:
            [
                new(TrayHoverLabelItemKind.GpuTemperature, true),
                new(TrayHoverLabelItemKind.ActiveProfile, true),
                new(TrayHoverLabelItemKind.BatteryPower, false),
            ],
            DashboardCards:
            [
                new(DashboardCardKind.Battery, true),
                new(DashboardCardKind.DeviceStatus, false),
            ],
            PrimaryNavigationItems:
            [
                PrimaryNavigationItemKind.Settings,
                PrimaryNavigationItemKind.Display,
                PrimaryNavigationItemKind.Laptop,
            ],
            AccentColorMode: AppAccentColorMode.Custom,
            CustomAccentColor: "#123abc",
            SpecialKeyBindings:
            [
                new(
                    SpecialKeySlot.M1,
                    new SpecialKeyAction(
                        SpecialKeyActionKind.KeyboardShortcut,
                        "Ctrl+Shift+M")),
                new(
                    SpecialKeySlot.M4,
                    new SpecialKeyAction(SpecialKeyActionKind.Disabled)),
            ],
            UpdateChannel: UpdateChannel.Nightly,
            UpdateCheckInterval: UpdateCheckInterval.Monthly,
            UpdateCheckPromptShown: true,
            LastSuccessfulUpdateCheckAt: new DateTimeOffset(
                2026,
                7,
                27,
                12,
                30,
                0,
                TimeSpan.Zero),
            UpdateCheckRetryAfter: new DateTimeOffset(
                2026,
                7,
                27,
                13,
                0,
                0,
                TimeSpan.Zero),
            UpdateCheckEntityTag: "\"release-etag\"",
            LastKnownReleaseTag: "v1.2.0",
            LastKnownReleaseUrl:
                "https://github.com/OSSBlade/openblade-core/releases/tag/v1.2.0",
            FirmwareUpdateCheckInterval: UpdateCheckInterval.Weekly,
            LastSuccessfulFirmwareUpdateCheckAt: new DateTimeOffset(
                2026,
                7,
                27,
                11,
                0,
                0,
                TimeSpan.Zero),
            FirmwareUpdateCheckRetryAfter: new DateTimeOffset(
                2026,
                7,
                27,
                11,
                5,
                0,
                TimeSpan.Zero),
            LastKnownFirmwareModelNumber: "RZ09-0581",
            LastKnownPublishedBiosVersion: "3.01",
            LastKnownPublishedEcVersion: "1.04",
            LastKnownPublishedMcuVersion: "1.06"));

        UiPreferences reloaded = await store.LoadAsync();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(ThemePreference.Dark, reloaded.Theme);
        Assert.False(reloaded.LiveReadingsEnabled);
        Assert.Equal(LiveReadingsCadence.Off, reloaded.EffectiveLiveReadingsCadence);
        Assert.Equal(TrayHoverLabelMode.BatteryPowerFlow, reloaded.TrayHoverLabelMode);
        Assert.True(reloaded.StartMinimized);
        Assert.Equal(
            [LiveReadingItemKind.BatteryPower, LiveReadingItemKind.GpuTemperature],
            reloaded.EffectiveDashboardTelemetryItems);
        Assert.Equal(
            [
                TrayHoverLabelItemKind.AppName,
                TrayHoverLabelItemKind.GpuTemperature,
                TrayHoverLabelItemKind.PowerSource,
                TrayHoverLabelItemKind.PowerMode,
                TrayHoverLabelItemKind.BatteryPower,
                TrayHoverLabelItemKind.CpuTemperature,
                TrayHoverLabelItemKind.Fan1Speed,
                TrayHoverLabelItemKind.Fan2Speed,
            ],
            reloaded.EffectiveTrayHoverLabelItems.Select(item => item.Kind));
        Assert.Equal(
            [true, true, true, true, false, false, false, false],
            reloaded.EffectiveTrayHoverLabelItems.Select(item => item.IsEnabled));
        Assert.Equal(
            [DashboardCardKind.Battery, DashboardCardKind.DeviceStatus],
            reloaded.EffectiveDashboardCards.Take(2).Select(item => item.Kind));
        Assert.Equal(
            [true, false],
            reloaded.EffectiveDashboardCards.Take(2).Select(item => item.IsPinned));
        Assert.Equal(
            [
                PrimaryNavigationItemKind.Settings,
                PrimaryNavigationItemKind.Display,
                PrimaryNavigationItemKind.Laptop,
                PrimaryNavigationItemKind.Profiles,
                PrimaryNavigationItemKind.Lighting,
            ],
            reloaded.EffectivePrimaryNavigationItems);
        Assert.Equal(AppAccentColorMode.Custom, reloaded.EffectiveAccentColorMode);
        Assert.Equal("#123ABC", reloaded.EffectiveCustomAccentColor);
        Assert.Equal(
            SpecialKeyActionKind.KeyboardShortcut,
            reloaded.EffectiveSpecialKeyBindings[(int)SpecialKeySlot.M1].Action.Kind);
        Assert.Equal(
            "Ctrl+Shift+M",
            reloaded.EffectiveSpecialKeyBindings[(int)SpecialKeySlot.M1].Action.Value);
        Assert.Equal(
            SpecialKeyActionKind.Disabled,
            reloaded.EffectiveSpecialKeyBindings[(int)SpecialKeySlot.M4].Action.Kind);
        Assert.Equal(UpdateChannel.Nightly, reloaded.EffectiveUpdateChannel);
        Assert.Equal(UpdateCheckInterval.Monthly, reloaded.EffectiveUpdateCheckInterval);
        Assert.True(reloaded.UpdateCheckPromptShown);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 12, 30, 0, TimeSpan.Zero),
            reloaded.LastSuccessfulUpdateCheckAt);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 13, 0, 0, TimeSpan.Zero),
            reloaded.UpdateCheckRetryAfter);
        Assert.Equal("\"release-etag\"", reloaded.UpdateCheckEntityTag);
        Assert.Equal("v1.2.0", reloaded.LastKnownReleaseTag);
        Assert.Equal(
            UpdateCheckInterval.Weekly,
            reloaded.EffectiveFirmwareUpdateCheckInterval);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 11, 0, 0, TimeSpan.Zero),
            reloaded.LastSuccessfulFirmwareUpdateCheckAt);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 11, 5, 0, TimeSpan.Zero),
            reloaded.FirmwareUpdateCheckRetryAfter);
        Assert.Equal("RZ09-0581", reloaded.LastKnownFirmwareModelNumber);
        Assert.Equal("3.01", reloaded.LastKnownPublishedBiosVersion);
        Assert.Equal("1.04", reloaded.LastKnownPublishedEcVersion);
        Assert.Equal("1.06", reloaded.LastKnownPublishedMcuVersion);
        Assert.Equal("Dark", document.RootElement.GetProperty("theme").GetString());
        Assert.False(document.RootElement.TryGetProperty(
            "keepManualChangesUntilCleared",
            out _));
        Assert.False(document.RootElement.GetProperty("liveReadingsEnabled").GetBoolean());
        Assert.Equal(
            "BatteryPowerFlow",
            document.RootElement.GetProperty("trayHoverLabelMode").GetString());
        Assert.Equal(
            "EveryTenSeconds",
            document.RootElement.GetProperty("liveReadingsRefreshCadence").GetString());
        Assert.True(document.RootElement.GetProperty("startMinimized").GetBoolean());
        Assert.Equal(
            "GpuTemperature",
            document.RootElement.GetProperty("dashboardTelemetryItems")[0].GetString());
        Assert.Equal(
            "GpuTemperature",
            document.RootElement.GetProperty("trayHoverLabelItems")[0]
                .GetProperty("kind").GetString());
        Assert.Equal(
            "Battery",
            document.RootElement.GetProperty("dashboardCards")[0]
                .GetProperty("kind").GetString());
        Assert.Equal(
            "Settings",
            document.RootElement.GetProperty("primaryNavigationItems")[0].GetString());
        Assert.Equal(
            "Custom",
            document.RootElement.GetProperty("accentColorMode").GetString());
        Assert.Equal(
            "#123abc",
            document.RootElement.GetProperty("customAccentColor").GetString());
        Assert.Equal(
            "M1",
            document.RootElement.GetProperty("specialKeyBindings")[0]
                .GetProperty("slot").GetString());
        Assert.Equal(
            "Nightly",
            document.RootElement.GetProperty("updateChannel").GetString());
        Assert.Equal(
            "Monthly",
            document.RootElement.GetProperty("updateCheckInterval").GetString());
        Assert.Equal(
            "Weekly",
            document.RootElement.GetProperty("firmwareUpdateCheckInterval").GetString());
        Assert.Equal(
            "2026-07-27T12:30:00+00:00",
            document.RootElement.GetProperty("lastSuccessfulUpdateCheckAt").GetString());
        Assert.True(
            document.RootElement.GetProperty("updateCheckPromptShown").GetBoolean());
    }

    [Fact]
    public async Task LoadAsync_InvalidUpdateIntervalFallsBackToDaily()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """{"theme":"Dark","updateCheckInterval":999}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(UpdateCheckInterval.Daily, preferences.EffectiveUpdateCheckInterval);
    }

    [Fact]
    public async Task LoadAsync_InvalidUpdateChannelFallsBackToStable()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """{"theme":"Dark","updateChannel":999}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(UpdateChannel.Stable, preferences.EffectiveUpdateChannel);
    }

    [Fact]
    public async Task LoadAsync_ExistingPreferencesWithoutUpdateKeysRequireConsent()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, """{"theme":"Dark"}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(UpdateCheckInterval.Daily, preferences.EffectiveUpdateCheckInterval);
        Assert.Equal(UpdateChannel.Stable, preferences.EffectiveUpdateChannel);
        Assert.False(preferences.UpdateCheckPromptShown);
        Assert.Null(preferences.LastSuccessfulUpdateCheckAt);
        Assert.Equal(
            UpdateCheckInterval.ManualOnly,
            preferences.EffectiveFirmwareUpdateCheckInterval);
    }

    [Fact]
    public async Task LoadAsync_NullPageSectionsMigrateToCurrentDefaults()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "theme": "Dark",
              "pageLayouts": [
                { "page": "Settings", "sections": null },
                { "page": "About", "sections": null }
              ]
            }
            """);

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        IReadOnlyList<PageLayoutPreference> actual = preferences.EffectivePageLayouts;
        Assert.Equal(PageLayoutPreferenceDefaults.Layouts.Count, actual.Count);
        foreach (PageLayoutPreference expected in PageLayoutPreferenceDefaults.Layouts)
        {
            Assert.Equal(
                expected.Sections.Select(section => (section.Kind, section.IsVisible)),
                actual
                    .Single(layout => layout.Page == expected.Page)
                    .Sections
                    .Select(section => (section.Kind, section.IsVisible)));
        }
    }

    [Fact]
    public async Task LoadAsync_InvalidCustomAccentFallsBackToRazerGreen()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """{"theme":"Dark","accentColorMode":"Custom","customAccentColor":"not-a-color"}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(AppAccentColorMode.Custom, preferences.EffectiveAccentColorMode);
        Assert.Equal(
            AppAccentColorPreferenceDefaults.RazerGreenHex,
            preferences.EffectiveCustomAccentColor);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_ReturnsFollowWindows()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "{ definitely not json }");
        UiPreferenceStore store = new(path);

        UiPreferences preferences = await store.LoadAsync();

        Assert.Equal(ThemePreference.FollowWindows, preferences.Theme);
    }

    [Fact]
    public async Task LoadAsync_ObsoleteManualPreferenceIsIgnored()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """{"theme":"Dark","keepManualChangesUntilCleared":true}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(ThemePreference.Dark, preferences.Theme);
        Assert.True(preferences.LiveReadingsEnabled);
        Assert.Equal(
            LiveReadingsCadence.EveryTwoSeconds,
            preferences.EffectiveLiveReadingsCadence);
        Assert.Equal(
            TrayHoverLabelMode.ProfileAndPowerFlow,
            preferences.TrayHoverLabelMode);
    }

    [Fact]
    public async Task LoadAsync_PreviousDisabledPreferenceRemainsOff()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """{"theme":"Dark","liveReadingsEnabled":false}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.False(preferences.LiveReadingsEnabled);
        Assert.Equal(LiveReadingsCadence.Off, preferences.EffectiveLiveReadingsCadence);
    }

    [Theory]
    [InlineData(TrayHoverLabelMode.OpenBlade, false, false)]
    [InlineData(TrayHoverLabelMode.ActiveProfile, true, false)]
    [InlineData(TrayHoverLabelMode.BatteryPowerFlow, false, true)]
    [InlineData(TrayHoverLabelMode.ProfileAndPowerFlow, true, true)]
    public async Task LoadAsync_LegacyHoverModeMigratesToOrderedItems(
        TrayHoverLabelMode mode,
        bool profileEnabled,
        bool powerEnabled)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            $$"""{"theme":"Dark","trayHoverLabelMode":"{{mode}}"}""");

        UiPreferences preferences = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(
            profileEnabled,
            preferences.EffectiveTrayHoverLabelItems.Single(
                item => item.Kind == TrayHoverLabelItemKind.PowerSource).IsEnabled);
        Assert.Equal(
            profileEnabled,
            preferences.EffectiveTrayHoverLabelItems.Single(
                item => item.Kind == TrayHoverLabelItemKind.PowerMode).IsEnabled);
        Assert.Equal(
            powerEnabled,
            preferences.EffectiveTrayHoverLabelItems.Single(
                item => item.Kind == TrayHoverLabelItemKind.BatteryPower).IsEnabled);
        Assert.True(preferences.EffectiveTrayHoverLabelItems.Single(
            item => item.Kind == TrayHoverLabelItemKind.AppName).IsEnabled);
        Assert.All(
            preferences.EffectiveTrayHoverLabelItems.Where(item =>
                item.Kind is not TrayHoverLabelItemKind.AppName
                    and not TrayHoverLabelItemKind.PowerSource
                    and not TrayHoverLabelItemKind.PowerMode
                    and not TrayHoverLabelItemKind.BatteryPower),
            item => Assert.False(item.IsEnabled));
    }

    [Fact]
    public async Task SaveAsync_ReplacesExistingFileWithoutLeavingTemporaryFiles()
    {
        string path = Path.Combine(directory, "preferences.json");
        UiPreferenceStore store = new(path);
        await store.SaveAsync(new UiPreferences(ThemePreference.Light));

        await store.SaveAsync(new UiPreferences(ThemePreference.Dark));

        Assert.Equal(ThemePreference.Dark, (await store.LoadAsync()).Theme);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_WrapsIoFailureInUiPreferenceSaveException()
    {
        Directory.CreateDirectory(directory);
        string blockingFile = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "blocking directory creation");
        string path = Path.Combine(blockingFile, "preferences.json");
        UiPreferenceStore store = new(path);

        UiPreferenceSaveException exception =
            await Assert.ThrowsAsync<UiPreferenceSaveException>(
                () => store.SaveAsync(new UiPreferences(ThemePreference.Dark)));

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_DoesNotWrapProgrammingErrors()
    {
        UiPreferenceStore store = new(Path.Combine(directory, "preferences.json"));
        var failure = new ProgrammingTestException("serializer failed");
        var preferences = new UiPreferences(
            ThemePreference.Dark,
            DashboardTelemetryItems:
                new ThrowingReadOnlyList<LiveReadingItemKind>(() => failure));

        ProgrammingTestException exception =
            await Assert.ThrowsAsync<ProgrammingTestException>(
                () => store.SaveAsync(preferences));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task SaveAsync_TemporaryFileCleanupFailureDoesNotMaskTheResult()
    {
        string? temporaryPath = null;
        UiPreferenceStore store = new(Path.Combine(directory, "preferences.json"));
        var failure = new ProgrammingTestException("serializer failed");
        var preferences = new UiPreferences(
            ThemePreference.Dark,
            DashboardTelemetryItems:
                new ThrowingReadOnlyList<LiveReadingItemKind>(() =>
                {
                    temporaryPath = Assert.Single(
                        Directory.EnumerateFiles(directory, "*.tmp"));
                    File.SetAttributes(temporaryPath, FileAttributes.ReadOnly);
                    return failure;
                }));

        try
        {
            ProgrammingTestException exception =
                await Assert.ThrowsAsync<ProgrammingTestException>(
                    () => store.SaveAsync(preferences));

            Assert.Same(failure, exception);
            Assert.NotNull(temporaryPath);
            Assert.True(File.Exists(temporaryPath));
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                File.SetAttributes(temporaryPath, FileAttributes.Normal);
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.EnumerateFiles(
                directory,
                "*",
                SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ThrowingReadOnlyList<T>(Func<Exception> createFailure)
        : IReadOnlyList<T>
    {
        public int Count => 1;

        public T this[int index] => throw createFailure();

        public IEnumerator<T> GetEnumerator() => throw createFailure();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ProgrammingTestException(string message) : Exception(message);
}
