using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class DiagnosticArchiveProviderTests
{
    [Fact]
    public async Task CreateAsync_UsesOnlyCurrentSnapshotsAndWritesExactlySixRedactedEntries()
    {
        const string secret = "S-1-5-21-sensitive-token";
        var runtime = new SnapshotOnlyRuntime(CreateStatus() with
        {
            ControllerDetectionWarning = $"Enumeration warning: {secret}",
            AlwaysOverrideExternalLightingChanges = true,
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                SpecialKeyBindingsAccess = DeviceFeatureAccess.ObservedOnly,
                ObservedLightingEffects =
                    new HashSet<LightingEffect>
                    {
                        LightingEffect.Spectrum,
                        LightingEffect.Static,
                    },
            },
        });
        var history = new DiagnosticHistory();
        history.RecordSummary(ControlOperationStatus.Blocked, $"Blocked for {secret}");
        var provider = new DiagnosticArchiveProvider(
            runtime,
            history,
            new FixedMetadataSource(secret));

        DiagnosticArchive result = await provider.CreateAsync(
            liveReadingsEnabled: true,
            CancellationToken.None);

        Assert.Equal(1, runtime.StatusReads);
        Assert.Equal(0, runtime.HardwareOrMutationCalls);
        Assert.EndsWith(".zip", result.SuggestedFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFileName(result.SuggestedFileName), result.SuggestedFileName);
        Assert.InRange(result.ZipBytes.Length, 1, OpenBladeIpc.MaximumDiagnosticArchiveBytes);

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        Assert.Equal(
            ["versions.json", "device.json", "capabilities.json", "configuration.json", "command-results.json", "logs.jsonl"],
            archive.Entries.Select(entry => entry.FullName));
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            string content = await reader.ReadToEndAsync(CancellationToken.None);
            Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
            Assert.DoesNotContain("rawHid", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transactionId", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("checksum", content, StringComparison.OrdinalIgnoreCase);
        }

        using JsonDocument device = JsonDocument.Parse(await ReadEntryAsync(archive, "device.json"));
        Assert.Equal("RZ09-0581", device.RootElement.GetProperty("model").GetString());
        Assert.Equal("1532", device.RootElement.GetProperty("vendorId").GetString());
        Assert.Equal("02E0", device.RootElement.GetProperty("productId").GetString());
        Assert.Equal(JsonValueKind.Null, device.RootElement.GetProperty("serialNumber").ValueKind);

        using JsonDocument configuration = JsonDocument.Parse(
            await ReadEntryAsync(archive, "configuration.json"));
        Assert.Equal(
            PowerSource.PluggedIn.ToString(),
            configuration.RootElement.GetProperty("power.source").GetString());
        Assert.Equal(
            bool.TrueString,
            configuration.RootElement.GetProperty("liveReadings.enabled").GetString());
        Assert.Equal(
            bool.TrueString,
            configuration.RootElement.GetProperty("liveReadings.available").GetString());
        Assert.Equal(
            "80",
            configuration.RootElement.GetProperty("confirmed.battery.chargeLimitPercent").GetString());
        Assert.Equal(
            string.Empty,
            configuration.RootElement.GetProperty("capability.chargeLimits").GetString());
        Assert.Equal(
            "RazerAppEngine",
            configuration.RootElement.GetProperty("status.potentialControllers").GetString());
        Assert.Equal(
            "Enumeration warning: [REDACTED]",
            configuration.RootElement.GetProperty("status.controllerDetectionWarning").GetString());
        Assert.Equal(
            "Fans",
            configuration.RootElement.GetProperty("status.externalChanges").GetString());
        Assert.Equal(
            PerformancePreset.Balanced.ToString(),
            configuration.RootElement.GetProperty("profile.usbC.performance").GetString());
        Assert.Equal(
            FanMode.Automatic.ToString(),
            configuration.RootElement.GetProperty("profile.usbC.fans").GetString());
        Assert.Equal(
            bool.TrueString,
            configuration.RootElement.GetProperty("lighting.alwaysOverrideExternalChanges").GetString());
        Assert.Equal(
            "Static, Spectrum",
            configuration.RootElement.GetProperty("capability.observedLightingEffects").GetString());
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly.ToString(),
            configuration.RootElement.GetProperty("capability.specialKeyBindingsAccess").GetString());
        using JsonDocument capabilities = JsonDocument.Parse(
            await ReadEntryAsync(archive, "capabilities.json"));
        Assert.True(
            capabilities.RootElement.GetProperty("lighting.observed.Spectrum").GetBoolean());
        Assert.False(
            capabilities.RootElement.GetProperty("lighting.Spectrum").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("laptop.specialKeyBindings.observed").GetBoolean());
        Assert.False(
            capabilities.RootElement.GetProperty("laptop.specialKeyBindings.write").GetBoolean());
        Assert.DoesNotContain(
            configuration.RootElement.EnumerateObject(),
            property => property.Name.StartsWith("telemetry.", StringComparison.Ordinal));
        string configurationJson = configuration.RootElement.GetRawText();
        Assert.DoesNotContain(
            "batteryPowerFlowMilliwatts",
            configurationJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fan1Rpm", configurationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fan2Rpm", configurationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12345", configurationJson, StringComparison.Ordinal);
        Assert.DoesNotContain("23456", configurationJson, StringComparison.Ordinal);
        Assert.DoesNotContain("34567", configurationJson, StringComparison.Ordinal);
        Assert.False(configuration.RootElement.TryGetProperty("status.conflicts", out _));
    }

    [Fact]
    public async Task CreateAsync_ReportsDisabledLiveReadingsWithoutTransientValues()
    {
        var provider = new DiagnosticArchiveProvider(
            new SnapshotOnlyRuntime(CreateStatus() with { LiveReadings = null }),
            new DiagnosticHistory(),
            new FixedMetadataSource());

        DiagnosticArchive result = await provider.CreateAsync(
            liveReadingsEnabled: false,
            CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        using JsonDocument configuration = JsonDocument.Parse(
            await ReadEntryAsync(archive, "configuration.json"));
        Assert.Equal(
            bool.FalseString,
            configuration.RootElement.GetProperty("liveReadings.enabled").GetString());
        Assert.Equal(
            bool.FalseString,
            configuration.RootElement.GetProperty("liveReadings.available").GetString());
    }

    [Fact]
    public async Task CreateAsync_DistinguishesEnabledButUnavailableLiveReadings()
    {
        var provider = new DiagnosticArchiveProvider(
            new SnapshotOnlyRuntime(CreateStatus() with
            {
                LiveReadings = new LiveReadings(null, null, null),
            }),
            new DiagnosticHistory(),
            new FixedMetadataSource());

        DiagnosticArchive result = await provider.CreateAsync(
            liveReadingsEnabled: true,
            CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        using JsonDocument configuration = JsonDocument.Parse(
            await ReadEntryAsync(archive, "configuration.json"));
        Assert.Equal(
            bool.TrueString,
            configuration.RootElement.GetProperty("liveReadings.enabled").GetString());
        Assert.Equal(
            bool.FalseString,
            configuration.RootElement.GetProperty("liveReadings.available").GetString());
    }

    [Fact]
    public async Task CreateAsync_RemainsAvailableWhenStatusIsReadOnlyAndUnavailable()
    {
        var runtime = new SnapshotOnlyRuntime(CreateStatus() with
        {
            DeviceVerified = false,
            WritesBlocked = true,
            Message = "No verified device is available.",
        });
        var provider = new DiagnosticArchiveProvider(
            runtime,
            new DiagnosticHistory(),
            new FixedMetadataSource());

        DiagnosticArchive result = await provider.CreateAsync(
            liveReadingsEnabled: true,
            CancellationToken.None);

        Assert.NotEmpty(result.ZipBytes);
        Assert.Equal(1, runtime.StatusReads);
        Assert.Equal(0, runtime.HardwareOrMutationCalls);
    }

    [Fact]
    public async Task CreateAsync_UsesObservedMcuVersionWithoutRefreshingHardware()
    {
        var runtime = new SnapshotOnlyRuntime(CreateStatus() with
        {
            Firmware = new FirmwareVersions(
                "3.01",
                "1.04",
                "1.5.0.0",
                "98.3.5c.40.3d"),
        });
        var provider = new DiagnosticArchiveProvider(
            runtime,
            new DiagnosticHistory(),
            new FixedMetadataSource());

        DiagnosticArchive result = await provider.CreateAsync(
            liveReadingsEnabled: false,
            CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        using JsonDocument versions = JsonDocument.Parse(
            await ReadEntryAsync(archive, "versions.json"));
        Assert.Equal("1.5.0.0", versions.RootElement.GetProperty("mcuVersion").GetString());
        Assert.Equal(1, runtime.StatusReads);
        Assert.Equal(0, runtime.HardwareOrMutationCalls);
    }

    [Fact]
    public async Task CreateAsync_RejectsArchiveLargerThanThirtyTwoKiB()
    {
        byte[] randomBytes = new byte[50_000];
        new Random(1_337).NextBytes(randomBytes);
        string incompressible = Convert.ToBase64String(randomBytes);
        var provider = new DiagnosticArchiveProvider(
            new SnapshotOnlyRuntime(CreateStatus()),
            new DiagnosticHistory(),
            new FixedMetadataSource(windowsVersion: incompressible));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.CreateAsync(
                liveReadingsEnabled: true,
                CancellationToken.None));

        Assert.Contains("32 KiB", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(archive.Entries, item => item.FullName == name);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    private static ServiceStatusSnapshot CreateStatus()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            12,
            DeviceVerified: true,
            WritesBlocked: true,
            PotentialControllers: ["RazerAppEngine"],
            ControllerDetectionWarning: null,
            ExternalChanges: [DeviceSubsystem.Fans],
            Message: "Verified read-only state.",
            Capabilities: DeviceCapabilities.Unavailable(),
            ConfirmedState: new ConfirmedDeviceState(
                null,
                null,
                new ConfirmedBatteryState(true, 80, false),
                null),
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: new LiveReadings(
                BatteryPowerFlowMilliwatts: 12345,
                Fan1Rpm: 23456,
                Fan2Rpm: 34567),
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false);
    }

    private sealed class FixedMetadataSource(
        string? secret = null,
        string windowsVersion = "Windows 11") : IDiagnosticMetadataSource
    {
        public DiagnosticHostMetadata Capture() => new(
            new DiagnosticVersions("1.0.0", windowsVersion, "2.00"),
            secret is null ? [] : [secret],
            DateTimeOffset.Parse("2026-07-15T12:34:56Z"));
    }

    private sealed class SnapshotOnlyRuntime(ServiceStatusSnapshot status) : IServiceControlRuntime
    {
        public int StatusReads { get; private set; }
        public int HardwareOrMutationCalls { get; private set; }

        public ServiceStatusSnapshot GetStatus()
        {
            StatusReads++;
            return status;
        }

        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) => Unexpected();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) => Unexpected();
        public Task ObserveAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken)
        {
            HardwareOrMutationCalls++;
            throw new InvalidOperationException("Export must not refresh hardware.");
        }

        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken)
        {
            HardwareOrMutationCalls++;
            throw new InvalidOperationException("Export must not refresh conflicts.");
        }

        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) =>
            ObserveAsync(includeLiveReadings: false, cancellationToken);

        private Task<ControlOperationResult> Unexpected()
        {
            HardwareOrMutationCalls++;
            throw new InvalidOperationException("Export must not mutate hardware.");
        }
    }
}
