using System.Collections;
using System.Text.Json;

namespace OpenBlade.Tray.Tests;

public sealed class UiPreferenceStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.Tray.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_PreservesAFileWithAFutureDashboardSchema(bool includeBom)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        string original = $$"""
            {
              "theme": "Dark",
              "trayDashboard": {
                "schemaVersion": {{TrayDashboardPreferenceDefaults.CurrentSchemaVersion + 1}},
                "futureOnly": "keep-me",
                "controls": [
                  { "kind": "FutureControl", "isVisible": true }
                ],
                "nested": { "mode": "FutureMode", "value": 17 }
              }
            }
            """;
        await File.WriteAllTextAsync(
            path,
            original,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: includeBom));
        var store = new UiPreferenceStore(path);
        using JsonDocument originalDocument = JsonDocument.Parse(original);
        string originalDashboard = originalDocument.RootElement
            .GetProperty("trayDashboard")
            .GetRawText();

        await store.SaveAsync(UiPreferences.Default with
        {
            Theme = ThemePreference.Light,
            StartMinimized = true,
        });

        using JsonDocument saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("Light", saved.RootElement.GetProperty("theme").GetString());
        Assert.True(saved.RootElement.GetProperty("startMinimized").GetBoolean());
        Assert.Equal(
            originalDashboard,
            saved.RootElement.GetProperty("trayDashboard").GetRawText());
        Assert.Empty(Directory.EnumerateFiles(directory, "*.oversized.bak"));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_RefusesFutureDashboardExpansionBeyondMaximumSize()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        string prefix = "{\"trayDashboard\":{\"schemaVersion\":"
            + (TrayDashboardPreferenceDefaults.CurrentSchemaVersion + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            + ",\"futureOnly\":\"";
        const string suffix = "\"}}";
        int fillerLength = UiPreferenceStore.MaximumFileSizeBytes
            - 1
            - System.Text.Encoding.UTF8.GetByteCount(prefix + suffix);
        byte[] original = System.Text.Encoding.UTF8.GetBytes(
            prefix + new string('x', fillerLength) + suffix);
        Assert.Equal(UiPreferenceStore.MaximumFileSizeBytes - 1, original.Length);
        await File.WriteAllBytesAsync(path, original);
        var store = new UiPreferenceStore(path);

        UiPreferenceSaveException exception =
            await Assert.ThrowsAsync<UiPreferenceSaveException>(() =>
                store.SaveAsync(UiPreferences.Default));

        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.oversized.bak"));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_FutureDashboardWithUnknownKind_LoadsOtherPreferences(
        bool includeBom)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              "theme": "Dark",
              "language": "Russian",
              "startMinimized": true,
              "trayDashboard": {
                "schemaVersion": {{TrayDashboardPreferenceDefaults.CurrentSchemaVersion + 1}},
                "controls": [
                  { "kind": "FutureControl", "isVisible": true }
                ]
              }
            }
            """, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: includeBom));

        var store = new UiPreferenceStore(path);
        UiPreferences loaded = await store.LoadAsync();

        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(UiLanguagePreference.Russian, loaded.EffectiveLanguage);
        Assert.Equal(UiLanguagePreference.Russian, store.LoadLanguagePreference());
        Assert.True(loaded.StartMinimized);
        Assert.False(loaded.LabFeaturesEnabled);
        Assert.Equal(
            TrayDashboardPreferenceDefaults.CurrentSchemaVersion + 1,
            loaded.TrayDashboard?.SchemaVersion);
        Assert.Throws<InvalidDataException>(() => loaded.EffectiveTrayDashboard);
    }

    [Fact]
    public async Task LoadAsync_AcceptsAFileAtTheMaximumSize()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        const string json = "{\"theme\":\"Dark\"}";
        byte[] content = new byte[UiPreferenceStore.MaximumFileSizeBytes];
        System.Text.Encoding.UTF8.GetBytes(json, content);
        content.AsSpan(json.Length).Fill((byte)' ');
        await File.WriteAllBytesAsync(path, content);

        UiPreferences loaded = await new UiPreferenceStore(path).LoadAsync();

        Assert.Equal(ThemePreference.Dark, loaded.Theme);
    }

    [Fact]
    public async Task LoadAsync_OversizedFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllBytesAsync(
            path,
            new byte[UiPreferenceStore.MaximumFileSizeBytes + 1]);

        UiPreferences loaded = await new UiPreferenceStore(path).LoadAsync();

        Assert.Same(UiPreferences.Default, loaded);
    }

    [Fact]
    public async Task SaveAsync_PreservesAnOversizedExistingFileInABackup()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        byte[] oversized = new byte[UiPreferenceStore.MaximumFileSizeBytes + 1];
        oversized[0] = (byte)'{';
        oversized[^1] = (byte)'}';
        await File.WriteAllBytesAsync(path, oversized);
        var store = new UiPreferenceStore(path);

        await store.SaveAsync(UiPreferences.Default with
        {
            Theme = ThemePreference.Light,
        });

        Assert.Equal(ThemePreference.Light, (await store.LoadAsync()).Theme);
        string backup = Assert.Single(Directory.EnumerateFiles(directory, "*.oversized.bak"));
        Assert.Equal(oversized, await File.ReadAllBytesAsync(backup));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_OversizedReplacementLeavesOriginalUntouchedWithoutABackup()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        byte[] oversized = new byte[UiPreferenceStore.MaximumFileSizeBytes + 1];
        oversized[0] = (byte)'{';
        oversized[^1] = (byte)'}';
        await File.WriteAllBytesAsync(path, oversized);
        var store = new UiPreferenceStore(path);

        UiPreferenceSaveException exception =
            await Assert.ThrowsAsync<UiPreferenceSaveException>(() =>
                store.SaveAsync(UiPreferences.Default with
                {
                    UpdateCheckEntityTag = new string('x', UiPreferenceStore.MaximumFileSizeBytes),
                }));

        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Equal(oversized, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.oversized.bak"));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_OversizedRecoveryFailureLeavesOriginalUntouched()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        byte[] oversized = new byte[UiPreferenceStore.MaximumFileSizeBytes + 1];
        await File.WriteAllBytesAsync(path, oversized);
        var store = new UiPreferenceStore(path);
        await using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        UiPreferenceSaveException exception =
            await Assert.ThrowsAsync<UiPreferenceSaveException>(() =>
                store.SaveAsync(UiPreferences.Default with { Theme = ThemePreference.Light }));

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.Equal(oversized, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.oversized.bak"));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_OversizedRecoveryDoesNotRecreateAMissingDestination()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        string movedPath = Path.Combine(directory, "moved-preferences.json");
        byte[] oversized = new byte[UiPreferenceStore.MaximumFileSizeBytes + 1];
        await File.WriteAllBytesAsync(path, oversized);
        var store = new UiPreferenceStore(path);

        UiPreferenceSaveException exception =
            await Assert.ThrowsAsync<UiPreferenceSaveException>(() =>
                store.SaveAsync(UiPreferences.Default with
                {
                    DashboardTelemetryItems = new CallbackReadOnlyList<LiveReadingItemKind>(
                        TelemetryPreferenceDefaults.DashboardItems,
                        () => File.Move(path, movedPath)),
                }));

        Assert.IsAssignableFrom<IOException>(exception.InnerException);
        Assert.False(File.Exists(path));
        Assert.Equal(oversized, await File.ReadAllBytesAsync(movedPath));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.oversized.bak"));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_OversizedRecoveryBacksUpTheCurrentDestination()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        string movedPath = Path.Combine(directory, "moved-preferences.json");
        byte[] oversized = new byte[UiPreferenceStore.MaximumFileSizeBytes + 1];
        byte[] replacement = System.Text.Encoding.UTF8.GetBytes(
            """{"theme":"Dark","futureData":"preserve-this-write"}""");
        await File.WriteAllBytesAsync(path, oversized);
        var store = new UiPreferenceStore(path);

        await store.SaveAsync(UiPreferences.Default with
        {
            Theme = ThemePreference.Light,
            DashboardTelemetryItems = new CallbackReadOnlyList<LiveReadingItemKind>(
                TelemetryPreferenceDefaults.DashboardItems,
                () =>
                {
                    File.Move(path, movedPath);
                    File.WriteAllBytes(path, replacement);
                }),
        });

        Assert.Equal(ThemePreference.Light, (await store.LoadAsync()).Theme);
        string backup = Assert.Single(Directory.EnumerateFiles(directory, "*.oversized.bak"));
        Assert.Equal(replacement, await File.ReadAllBytesAsync(backup));
        Assert.Equal(oversized, await File.ReadAllBytesAsync(movedPath));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsFollowWindows()
    {
        UiPreferenceStore store = new(Path.Combine(directory, "preferences.json"));

        UiPreferences preferences = await store.LoadAsync();

        Assert.Equal(ThemePreference.FollowWindows, preferences.Theme);
        Assert.Equal(UiLanguagePreference.FollowSystem, preferences.EffectiveLanguage);
        Assert.True(preferences.LiveReadingsEnabled);
        Assert.Equal(
            LiveReadingsCadence.EveryTwoSeconds,
            preferences.EffectiveLiveReadingsCadence);
        Assert.Equal(
            TrayHoverLabelMode.ProfileAndPowerFlow,
            preferences.TrayHoverLabelMode);
        Assert.False(preferences.StartMinimized);
        Assert.False(preferences.LabFeaturesEnabled);
        Assert.Equal(UpdateChannel.Stable, preferences.EffectiveUpdateChannel);
        Assert.Equal(UpdateCheckInterval.Daily, preferences.EffectiveUpdateCheckInterval);
        Assert.Equal(
            UpdateCheckInterval.ManualOnly,
            preferences.EffectiveFirmwareUpdateCheckInterval);
        Assert.Equal(
            NotificationPreferences.Default,
            preferences.EffectiveNotifications);
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
            TrayDashboardPreferenceDefaults.Preference.Controls,
            preferences.EffectiveTrayDashboard.Controls);
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

    [Theory]
    [InlineData(UiLanguagePreference.FollowSystem)]
    [InlineData(UiLanguagePreference.English)]
    [InlineData(UiLanguagePreference.SimplifiedChinese)]
    [InlineData(UiLanguagePreference.Russian)]
    public async Task LanguagePreference_RoundTripsThroughAsyncAndStartupReads(
        UiLanguagePreference preference)
    {
        string path = Path.Combine(directory, "preferences.json");
        var store = new UiPreferenceStore(path);

        await store.SaveAsync(UiPreferences.Default with { Language = preference });

        Assert.Equal(preference, (await store.LoadAsync()).EffectiveLanguage);
        Assert.Equal(preference, store.LoadLanguagePreference());
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(preference.ToString(), document.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public async Task LanguagePreference_UnknownNumericValueFallsBackWithoutDroppingOtherPreferences()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, """{"theme":"Dark","language":999}""");
        var store = new UiPreferenceStore(path);

        UiPreferences preferences = await store.LoadAsync();

        Assert.Equal(ThemePreference.Dark, preferences.Theme);
        Assert.Equal(UiLanguagePreference.FollowSystem, preferences.EffectiveLanguage);
        Assert.Equal(UiLanguagePreference.FollowSystem, store.LoadLanguagePreference());
    }

    [Fact]
    public async Task LoadLanguagePreference_MissingOrMalformedFileFallsBackToSystem()
    {
        string path = Path.Combine(directory, "preferences.json");
        var store = new UiPreferenceStore(path);
        Assert.Equal(UiLanguagePreference.FollowSystem, store.LoadLanguagePreference());

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{ definitely not json }");

        Assert.Equal(UiLanguagePreference.FollowSystem, store.LoadLanguagePreference());
    }

    [Fact]
    public async Task LoadLanguagePreference_OversizedFileFallsBackWithoutParsing()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllBytesAsync(
            path,
            new byte[UiPreferenceStore.MaximumFileSizeBytes + 1]);

        UiLanguagePreference language =
            new UiPreferenceStore(path).LoadLanguagePreference();

        Assert.Equal(UiLanguagePreference.FollowSystem, language);
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
            LastKnownFirmwareModelNumber: "RZ09-0528",
            LastKnownPublishedBiosVersion: "2.02",
            LastKnownPublishedEcVersion: "1.09",
            LastKnownPublishedMcuVersion: "1.09",
            LastKnownPublishedVbiosVersion: "98.03.5C.40.25",
            LastKnownPublishedVbiosGpuName: "RTX 5090",
            LastKnownPublishedThunderboltVersion: "69.1",
            LastKnownPublishedTouchpadVersion: "1.07",
            LastKnownPublishedBiosStatus: RazerPublishedFirmwareVersionStatus.Listed,
            LastKnownPublishedEcStatus: RazerPublishedFirmwareVersionStatus.Listed,
            LastKnownPublishedMcuStatus: RazerPublishedFirmwareVersionStatus.Listed,
            LastKnownPublishedVbiosStatus: RazerPublishedFirmwareVersionStatus.Listed,
            LastKnownPublishedThunderboltStatus:
                RazerPublishedFirmwareVersionStatus.Listed,
            LastKnownPublishedTouchpadStatus:
                RazerPublishedFirmwareVersionStatus.Listed,
            Notifications: NotificationPreferences.Default with
            {
                PerformanceChangesEnabled = false,
                ProfileAndPowerSourceChangesEnabled = true,
                AppUpdatesEnabled = false,
            },
            LastNotifiedReleaseTag: "v1.2.0",
            LastNotifiedFirmwareSignature:
                "RZ09-0528|bios=2.02|ec=1.09|mcu=1.09|vbios=98.03.5C.40.25|vbiosGpu=RTX 5090",
            TrayDashboard: new TrayDashboardPreference(
                Controls:
                [
                    new(
                        TrayDashboardControlKind.Battery,
                        true),
                    new(
                        TrayDashboardControlKind.Performance,
                        false),
                ],
                StatusItems:
                [
                    new(TrayDashboardStatusItemKind.BatteryPower, true),
                    new(TrayDashboardStatusItemKind.FanSpeed, false),
                ],
                Sliders:
                [
                    new(TrayDashboardSliderKind.GpuMemoryClock, true),
                    new(TrayDashboardSliderKind.ChargeLimit, true),
                ],
                SchemaVersion: TrayDashboardPreferenceDefaults.CurrentSchemaVersion),
            LabFeaturesEnabled: true));

        UiPreferences reloaded = await store.LoadAsync();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(ThemePreference.Dark, reloaded.Theme);
        Assert.False(reloaded.LiveReadingsEnabled);
        Assert.Equal(LiveReadingsCadence.Off, reloaded.EffectiveLiveReadingsCadence);
        Assert.Equal(TrayHoverLabelMode.BatteryPowerFlow, reloaded.TrayHoverLabelMode);
        Assert.True(reloaded.StartMinimized);
        Assert.True(reloaded.LabFeaturesEnabled);
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
        Assert.Equal("RZ09-0528", reloaded.LastKnownFirmwareModelNumber);
        Assert.Equal("2.02", reloaded.LastKnownPublishedBiosVersion);
        Assert.Equal("1.09", reloaded.LastKnownPublishedEcVersion);
        Assert.Equal("1.09", reloaded.LastKnownPublishedMcuVersion);
        Assert.Equal("98.03.5C.40.25", reloaded.LastKnownPublishedVbiosVersion);
        Assert.Equal("RTX 5090", reloaded.LastKnownPublishedVbiosGpuName);
        Assert.Equal("69.1", reloaded.LastKnownPublishedThunderboltVersion);
        Assert.Equal("1.07", reloaded.LastKnownPublishedTouchpadVersion);
        Assert.Equal(
            RazerPublishedFirmwareVersionStatus.Listed,
            reloaded.LastKnownPublishedBiosStatus);
        Assert.Equal(
            RazerPublishedFirmwareVersionStatus.Listed,
            reloaded.LastKnownPublishedEcStatus);
        Assert.Equal(
            RazerPublishedFirmwareVersionStatus.Listed,
            reloaded.LastKnownPublishedMcuStatus);
        Assert.Equal(
            RazerPublishedFirmwareVersionStatus.Listed,
            reloaded.LastKnownPublishedVbiosStatus);
        Assert.Equal(
            RazerPublishedFirmwareVersionStatus.Listed,
            reloaded.LastKnownPublishedThunderboltStatus);
        Assert.Equal(
            RazerPublishedFirmwareVersionStatus.Listed,
            reloaded.LastKnownPublishedTouchpadStatus);
        Assert.False(reloaded.EffectiveNotifications.PerformanceChangesEnabled);
        Assert.True(reloaded.EffectiveNotifications.ProfileAndPowerSourceChangesEnabled);
        Assert.False(reloaded.EffectiveNotifications.AppUpdatesEnabled);
        Assert.True(reloaded.EffectiveNotifications.FirmwareUpdatesEnabled);
        Assert.Equal("v1.2.0", reloaded.LastNotifiedReleaseTag);
        Assert.Equal(
            "RZ09-0528|bios=2.02|ec=1.09|mcu=1.09|vbios=98.03.5C.40.25|vbiosGpu=RTX 5090",
            reloaded.LastNotifiedFirmwareSignature);
        Assert.Equal(
            TrayDashboardControlKind.Performance,
            reloaded.EffectiveTrayDashboard.Controls![0].Kind);
        Assert.Equal(
            TrayDashboardPreferenceDefaults.CurrentSchemaVersion,
            reloaded.EffectiveTrayDashboard.SchemaVersion);
        Assert.True(reloaded.EffectiveTrayDashboard.Sliders!
            .Single(slider => slider.Kind == TrayDashboardSliderKind.ChargeLimit)
            .IsVisible);
        Assert.Equal(
            TrayDashboardSliderKind.GpuMemoryClock,
            reloaded.EffectiveTrayDashboard.Sliders![0].Kind);
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
        Assert.True(document.RootElement.GetProperty("labFeaturesEnabled").GetBoolean());
        Assert.Equal(
            "GpuMemoryClock",
            document.RootElement
                .GetProperty("trayDashboard")
                .GetProperty("sliders")[0]
                .GetProperty("kind")
                .GetString());
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
            "Battery",
            document.RootElement.GetProperty("trayDashboard")
                .GetProperty("controls")[0]
                .GetProperty("kind")
                .GetString());
        Assert.Equal(
            TrayDashboardPreferenceDefaults.CurrentSchemaVersion,
            document.RootElement.GetProperty("trayDashboard")
                .GetProperty("schemaVersion")
                .GetInt32());
        Assert.False(document.RootElement.GetProperty("trayDashboard")
            .TryGetProperty("expandedControl", out _));
        Assert.False(document.RootElement.GetProperty("trayDashboard")
            .GetProperty("controls")[0]
            .TryGetProperty("size", out _));
        Assert.Equal(
            "2026-07-27T12:30:00+00:00",
            document.RootElement.GetProperty("lastSuccessfulUpdateCheckAt").GetString());
        Assert.True(
            document.RootElement.GetProperty("updateCheckPromptShown").GetBoolean());
    }

    [Fact]
    public async Task LoadAsync_LegacyDashboardLayoutMigratesToCurrentSchema()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "theme": "Dark",
              "trayDashboard": {
                "controls": [
                  { "kind": "Lighting", "isVisible": true, "size": "Wide" },
                  { "kind": "Performance", "isVisible": false, "size": "Compact" }
                ],
                "expandedControl": "Lighting",
                "statusItems": [
                  { "kind": "Fans", "isVisible": true }
                ]
              }
            }
            """);
        UiPreferenceStore store = new(path);

        UiPreferences loaded = await store.LoadAsync();
        TrayDashboardPreference migrated = loaded.EffectiveTrayDashboard;

        Assert.Equal(0, loaded.TrayDashboard!.SchemaVersion);
        Assert.Equal(
            TrayDashboardPreferenceDefaults.CurrentSchemaVersion,
            migrated.SchemaVersion);
        Assert.Equal(
            [
                TrayDashboardControlKind.Lighting,
                TrayDashboardControlKind.Performance,
                TrayDashboardControlKind.Fans,
                TrayDashboardControlKind.BatteryProtection,
                TrayDashboardControlKind.FunctionRow,
                TrayDashboardControlKind.GamingMode,
                TrayDashboardControlKind.GpuTuning,
            ],
            migrated.Controls!.Select(control => control.Kind));
        Assert.Equal(
            [true, false, false, false, false, false, false],
            migrated.Controls!.Select(control => control.IsVisible));
        Assert.True(migrated.Sliders!
            .Single(slider => slider.Kind == TrayDashboardSliderKind.LightingBrightness)
            .IsVisible);
        Assert.Equal(
            [
                (TrayDashboardStatusItemKind.FanSpeed, true),
                (TrayDashboardStatusItemKind.BatteryPower, false),
                (TrayDashboardStatusItemKind.Temperatures, false),
                (TrayDashboardStatusItemKind.PowerSource, false),
            ],
            migrated.StatusItems!.Select(status => (status.Kind, status.IsVisible)));

        await store.SaveAsync(loaded with { TrayDashboard = migrated });
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        JsonElement dashboard = document.RootElement.GetProperty("trayDashboard");
        Assert.Equal(
            TrayDashboardPreferenceDefaults.CurrentSchemaVersion,
            dashboard.GetProperty("schemaVersion").GetInt32());
        Assert.False(dashboard.TryGetProperty("expandedControl", out _));
        Assert.All(
            dashboard.GetProperty("controls").EnumerateArray(),
            control => Assert.False(control.TryGetProperty("size", out _)));
        string savedStatusItems = dashboard.GetProperty("statusItems").GetRawText();
        Assert.Contains("FanSpeed", savedStatusItems, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Fans\"", savedStatusItems, StringComparison.Ordinal);
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
        Assert.Equal(
            NotificationPreferences.Default,
            preferences.EffectiveNotifications);
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
    public async Task LoadAsync_SharingViolationPropagatesIOException()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "{\"theme\":\"Dark\"}");
        await using var exclusive = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        await Assert.ThrowsAsync<IOException>(
            () => new UiPreferenceStore(path).LoadAsync());
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

    private sealed class CallbackReadOnlyList<T>(IReadOnlyList<T> items, Action beforeEnumeration)
        : IReadOnlyList<T>
    {
        private bool invoked;

        public int Count => items.Count;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            if (!invoked)
            {
                invoked = true;
                beforeEnumeration();
            }

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
