using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

public sealed class DiagnosticArchiveProviderTests
{
    [Fact]
    public async Task CreateAsync_ExportsRetainedFirmwareRepliesWithoutHardwareAccessOrHexRedaction()
    {
        var diagnostics = new FirmwareQueryDiagnostics();
        var command = new RazerCommandSpec(0x07, 0x8C, new byte[] { 0, 0 }, 2);
        FirmwareQueryObservation rejected = diagnostics.Record(command,
            new(default, null) { QueryResponsePayloadHex = "AB11" }, DateTimeOffset.UnixEpoch)!;
        diagnostics.RecordRejection(rejected, FirmwareQueryOutcome.UnrecognizedValue);
        diagnostics.Record(command, new(default, null) { QueryResponsePayloadHex = "1111" },
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var runtime = new SnapshotOnlyRuntime(CreateStatus());
        var history = new DiagnosticHistory(maximumEntries: 1);
        history.RecordSummary(ControlOperationStatus.Blocked, "previous failure");
        history.RecordSummary(ControlOperationStatus.Applied, "recovered");
        var provider = new DiagnosticArchiveProvider(runtime, history,
            new FixedMetadataSource("AB"), diagnostics);

        DiagnosticArchive result = await provider.CreateAsync(false, default);

        Assert.Equal(0, runtime.HardwareOrMutationCalls);
        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        using JsonDocument json = JsonDocument.Parse(await ReadEntryAsync(archive, "firmware-queries.json"));
        JsonElement query = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("1111", query.GetProperty("latest").GetProperty("responsePayloadHex").GetString());
        Assert.Equal("AB11", query.GetProperty("lastFailure").GetProperty("responsePayloadHex").GetString());
        Assert.Equal("UnrecognizedValue", query.GetProperty("lastFailure").GetProperty("outcome").GetString());
        Assert.Equal(2, query.GetProperty("observationCount").GetInt32());
        Assert.InRange(result.ZipBytes.Length, 1, OpenBladeIpc.MaximumDiagnosticArchiveBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_ExportsExperimentalEvidenceWithoutProbingOrUploading(bool liveReadingsEnabled)
    {
        const string secret = "private-test-token";
        var support = new DiagnosticExperimentalSupport(
            "RZ09-0483", "UpstreamDerived", true, true,
            [new(DateTimeOffset.UnixEpoch, "Battery", "Readback", "Mismatch", true, "80", "60", secret)]);
        var runtime = new SnapshotOnlyRuntime(CreateStatus() with
        {
            IsExperimentalDevice = true,
            AllowExperimentalFeatures = true,
            ExperimentalWritesPaused = true,
            ExperimentalSupportSource = "UpstreamDerived",
        }, experimentalSupport: support);
        var provider = new DiagnosticArchiveProvider(runtime, new DiagnosticHistory(), new FixedMetadataSource(secret));

        DiagnosticArchive result = await provider.CreateAsync(liveReadingsEnabled, CancellationToken.None);

        Assert.Equal(0, runtime.HardwareOrMutationCalls);
        Assert.InRange(result.ZipBytes.Length, 1, OpenBladeIpc.MaximumDiagnosticArchiveBytes);
        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        string content = await ReadEntryAsync(archive, "experimental-support.json");
        Assert.DoesNotContain(secret, content);
        using JsonDocument json = JsonDocument.Parse(content);
        Assert.True(json.RootElement.GetProperty("support").GetProperty("writesPaused").GetBoolean());
        Assert.Equal("UpstreamDerived", json.RootElement.GetProperty("support").GetProperty("evidenceLevel").GetString());
        Assert.Equal("Mismatch", json.RootElement.GetProperty("support").GetProperty("observations")[0].GetProperty("outcome").GetString());
        Assert.Equal(liveReadingsEnabled ? JsonValueKind.Object : JsonValueKind.Null,
            json.RootElement.GetProperty("liveReadings").ValueKind);
    }

    [Fact]
    public async Task CreateAsync_UsesOnlyCurrentSnapshotsAndWritesRedactedEntries()
    {
        const string secret = "S-1-5-21-sensitive-token";
        var runtime = new SnapshotOnlyRuntime(CreateStatus() with
        {
            ControllerDetectionWarning = $"Enumeration warning: {secret}",
            AlwaysOverrideExternalLightingChanges = true,
            AllowUntestedFirmware = true,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys,
            KeyboardDriverModeActive = true,
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                SupportsKeyboardBrightness = true,
                SupportsPlaybackMuteIndicator = true,
                HyperBoostAccess = DeviceFeatureAccess.ReadWrite,
                HyperBoostWithoutCoolingPadAccess = DeviceFeatureAccess.ReadWrite,
                HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                    DeviceFeatureAccess.ObservedOnly,
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ObservedOnly),
                SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite,
                WritableSpecialKeySlots =
                    SpecialKeySlotSet.M3
                    | SpecialKeySlotSet.M4
                    | SpecialKeySlotSet.M5,
                LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
                ThxAudio = new ThxAudioCapabilities(
                    DeviceFeatureAccess.ReadWrite,
                    ThxAudioProductIds.Rznb12),
                ObservedLightingEffects =
                    new HashSet<LightingEffect>
                    {
                        LightingEffect.Spectrum,
                        LightingEffect.Static,
                    },
            },
        }, hyperBoostJournalDiagnostics: new DiagnosticHyperBoostOwnershipJournal(
            Exists: true,
            Valid: true,
            State: "OwnedConfirmed",
            BaseMode: PerformancePreset.Balanced,
            RequiresCoolingPad: false,
            RestoreIntent: new DiagnosticHyperBoostRestoreIntent(
                PerformancePreset.Silent,
                CustomPerformance: null,
                GpuClockTuning: null,
                FanControlSettings.Default),
            Sha256: new string('A', 64)));
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
        Assert.Equal(1, runtime.VerificationDiagnosticReads);
        Assert.Equal(1, runtime.JournalDiagnosticReads);
        Assert.Equal(0, runtime.HardwareOrMutationCalls);
        Assert.EndsWith(".zip", result.SuggestedFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFileName(result.SuggestedFileName), result.SuggestedFileName);
        Assert.InRange(result.ZipBytes.Length, 1, OpenBladeIpc.MaximumDiagnosticArchiveBytes);

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        Assert.Equal(
            ["versions.json", "device.json", "device-verification.json", "capabilities.json", "configuration.json", "command-results.json", "service-history.json", "firmware-queries.json", "hyperboost-ownership.json", "logs.jsonl"],
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
        Assert.Equal("RZ09-0528", device.RootElement.GetProperty("model").GetString());
        Assert.Equal("1532", device.RootElement.GetProperty("vendorId").GetString());
        Assert.Equal("02C6", device.RootElement.GetProperty("productId").GetString());
        Assert.Equal(JsonValueKind.Null, device.RootElement.GetProperty("serialNumber").ValueKind);

        using JsonDocument verification = JsonDocument.Parse(
            await ReadEntryAsync(archive, "device-verification.json"));
        Assert.Equal(JsonValueKind.Null, verification.RootElement.GetProperty("latestAttempt").ValueKind);

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
            bool.TrueString,
            configuration.RootElement.GetProperty("capability.supportsKeyboardBrightness").GetString());
        Assert.Equal(
            bool.TrueString,
            configuration.RootElement.GetProperty("capability.supportsPlaybackMuteIndicator").GetString());
        Assert.Equal(
            "RazerAppEngine",
            configuration.RootElement.GetProperty("status.potentialControllers").GetString());
        Assert.Equal(
            "Unavailable",
            configuration.RootElement.GetProperty("status.hyperBoostBlockingControllers").GetString());
        Assert.Equal(
            bool.FalseString,
            configuration.RootElement.GetProperty("status.hyperBoostRestorePending").GetString());
        using JsonDocument ownership = JsonDocument.Parse(
            await ReadEntryAsync(archive, "hyperboost-ownership.json"));
        Assert.Equal(
            "OwnedConfirmed",
            ownership.RootElement.GetProperty("state").GetString());
        Assert.Equal(
            new string('A', 64),
            ownership.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(
            "Enumeration warning: [REDACTED]",
            configuration.RootElement.GetProperty("status.controllerDetectionWarning").GetString());
        Assert.Equal(
            "Fans",
            configuration.RootElement.GetProperty("status.externalChanges").GetString());
        Assert.Equal(
            "Battery",
            configuration.RootElement.GetProperty("status.unconfirmedWrites").GetString());
        Assert.Equal(
            FunctionKeyPrimaryMode.MultimediaKeys.ToString(),
            configuration.RootElement.GetProperty("laptop.functionKeyPrimary").GetString());
        Assert.Equal(
            bool.TrueString,
            configuration.RootElement.GetProperty("laptop.keyboardDriverModeActive").GetString());
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
            bool.TrueString,
            configuration.RootElement.GetProperty("firmware.allowUntested").GetString());
        Assert.Equal(
            "Static, Spectrum",
            configuration.RootElement.GetProperty("capability.observedLightingEffects").GetString());
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite.ToString(),
            configuration.RootElement.GetProperty("capability.specialKeyBindingsAccess").GetString());
        Assert.Equal(
            "M3, M4, M5",
            configuration.RootElement.GetProperty("capability.writableSpecialKeySlots").GetString());
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly.ToString(),
            configuration.RootElement.GetProperty("capability.lampArrayLightingAccess").GetString());
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite.ToString(),
            configuration.RootElement.GetProperty("capability.thxAudioAccess").GetString());
        Assert.Equal(
            ThxAudioProductIds.Rznb12,
            configuration.RootElement.GetProperty("capability.thxAudioProductId").GetString());
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite.ToString(),
            configuration.RootElement.GetProperty("capability.hyperBoostAccess").GetString());
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite.ToString(),
            configuration.RootElement
                .GetProperty("capability.hyperBoostWithoutCoolingPadAccess")
                .GetString());
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite.ToString(),
            configuration.RootElement
                .GetProperty("capability.hyperBoostBase.Balanced")
                .GetString());
        using JsonDocument capabilities = JsonDocument.Parse(
            await ReadEntryAsync(archive, "capabilities.json"));
        Assert.True(
            capabilities.RootElement.GetProperty("lighting.observed.Spectrum").GetBoolean());
        Assert.False(
            capabilities.RootElement.GetProperty("lighting.Spectrum").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("lighting.keyboardBrightness").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("lighting.playbackMuteIndicator").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("laptop.specialKeyBindings.observed").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("laptop.specialKeyBindings.write").GetBoolean());
        Assert.False(
            capabilities.RootElement.GetProperty("laptop.specialKeyBindings.write.M1").GetBoolean());
        Assert.False(
            capabilities.RootElement.GetProperty("laptop.specialKeyBindings.write.M2").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("laptop.specialKeyBindings.write.M3").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("laptop.specialKeyBindings.write.M4").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("laptop.specialKeyBindings.write.M5").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("lighting.lampArray.observed").GetBoolean());
        Assert.False(
            capabilities.RootElement.GetProperty("lighting.lampArray.write").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("audio.thx.observed").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("audio.thx.write").GetBoolean());
        Assert.True(
            capabilities.RootElement.GetProperty("performance.hyperBoost.write").GetBoolean());
        Assert.True(
            capabilities.RootElement
                .GetProperty("performance.hyperBoost.withoutCoolingPad.write")
                .GetBoolean());
        Assert.True(
            capabilities.RootElement
                .GetProperty("performance.hyperBoost.withoutCoolingPad.gate.immediateNoLoad")
                .GetBoolean());
        Assert.True(
            capabilities.RootElement
                .GetProperty("performance.hyperBoost.withoutCoolingPad.gate.lowPowerLifecycle")
                .GetBoolean());
        Assert.True(
            capabilities.RootElement
                .GetProperty("performance.hyperBoost.withoutCoolingPad.gate.crashRecovery")
                .GetBoolean());
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
    public async Task CreateAsync_ExportsCachedRz090581CapabilityDiagnosticsWithoutHardwareIo()
    {
        var diagnostics = new Rz090581CapabilityDiagnostics(
            0x01,
            0x00,
            [0x01, 0x02]);
        var runtime = new SnapshotOnlyRuntime(
            CreateStatus() with
            {
                Firmware = FirmwareVersions.Unavailable() with
                {
                    ModelNumber = RazerBladeModelCatalog.OpenBladeTargetModelNumber,
                },
            },
            diagnostics);
        var provider = new DiagnosticArchiveProvider(
            runtime,
            new DiagnosticHistory(),
            new FixedMetadataSource());

        DiagnosticArchive result = await provider.CreateAsync(
            liveReadingsEnabled: false,
            CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        using JsonDocument configuration = JsonDocument.Parse(
            await ReadEntryAsync(archive, "configuration.json"));
        Assert.Equal(
            "0x01",
            configuration.RootElement
                .GetProperty("capability.rz09-0581.skuHardwareConfigurationRaw")
                .GetString());
        Assert.Equal(
            "0x00",
            configuration.RootElement
                .GetProperty("capability.rz09-0581.miniLedPanelResolutionRaw")
                .GetString());
        Assert.Equal(
            "0x01, 0x02",
            configuration.RootElement
                .GetProperty("capability.rz09-0581.thermalFanIds")
                .GetString());
        Assert.Equal(1, runtime.StatusReads);
        Assert.Equal(1, runtime.CapabilityDiagnosticReads);
        Assert.Equal(0, runtime.HardwareOrMutationCalls);
    }

    [Fact]
    public async Task CreateAsync_RoundTripsRetainedExceptionArchiveLargerThanLegacyFrameLimit()
    {
        var runtime = new SnapshotOnlyRuntime(CreateStatus());
        var history = new DiagnosticHistory();
        ILogger logger = history.CreateLogger("OpenBlade.Service.ArchiveRegression");
        try
        {
            ThrowRetainedArchiveFailure();
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Archive source failure was retained.");
        }
        for (int index = 0; index < 200; index++)
        {
            logger.LogInformation(
                "entropy-{Index:D3} {Payload}",
                index,
                CreateDeterministicEntropy(index));
        }
        DiagnosticHistorySnapshot expectedHistory = history.Snapshot();
        var provider = new DiagnosticArchiveProvider(
            runtime,
            history,
            new FixedMetadataSource());

        DiagnosticArchive produced = await provider.CreateAsync(
            liveReadingsEnabled: false,
            CancellationToken.None);
        Assert.InRange(
            produced.ZipBytes.Length,
            32 * 1024 + 1,
            OpenBladeIpc.MaximumDiagnosticArchiveBytes);

        Guid requestId = Guid.Parse("13d74214-4e19-48ec-afc4-734a02fe1168");
        using var wire = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(
            wire,
            new ServiceResponse(
                OpenBladeIpc.ProtocolVersion,
                requestId,
                Success: true,
                Error: null,
                Status: null,
                Result: null,
                Archive: produced),
            CancellationToken.None);
        wire.Position = 0;
        ServiceResponse response = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadDiagnosticArchiveFrameAsync(
                wire,
                CancellationToken.None));

        Assert.Equal(requestId, response.RequestId);
        DiagnosticArchive returned = Assert.IsType<DiagnosticArchive>(response.Archive);
        Assert.Equal(produced.SuggestedFileName, returned.SuggestedFileName);
        Assert.Equal(produced.ZipBytes, returned.ZipBytes);
        using var archive = new ZipArchive(
            new MemoryStream(returned.ZipBytes),
            ZipArchiveMode.Read);
        using JsonDocument serviceHistory = JsonDocument.Parse(
            await ReadEntryAsync(archive, "service-history.json"));
        Assert.Equal(
            expectedHistory.EvictedEntries,
            serviceHistory.RootElement.GetProperty("evictedEntries").GetInt64());
        Assert.Equal(
            expectedHistory.DroppedEntries,
            serviceHistory.RootElement.GetProperty("droppedEntries").GetInt64());
        Assert.Equal(
            expectedHistory.TruncatedValues,
            serviceHistory.RootElement.GetProperty("truncatedValues").GetInt64());
        Assert.Equal(
            expectedHistory.RetainedEntries,
            serviceHistory.RootElement.GetProperty("retainedEntries").GetInt32());
        Assert.Equal(
            expectedHistory.RetainedCharacters,
            serviceHistory.RootElement.GetProperty("retainedCharacters").GetInt32());
        string retainedLogs = await ReadEntryAsync(archive, "logs.jsonl");
        Assert.Contains("archive-source-exception", retainedLogs, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowRetainedArchiveFailure), retainedLogs, StringComparison.Ordinal);
        Assert.Equal(0, runtime.HardwareOrMutationCalls);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRetainedArchiveFailure()
    {
        throw new InvalidOperationException("archive-source-exception");
    }

    private static string CreateDeterministicEntropy(int line)
    {
        byte[] payload = new byte[8 * 32];
        for (int block = 0; block < 8; block++)
        {
            byte[] input = Encoding.UTF8.GetBytes($"archive-line-{line:D3}-block-{block:D2}");
            byte[] hash = SHA256.HashData(input);
            hash.CopyTo(payload, block * hash.Length);
        }
        return Convert.ToBase64String(payload);
    }

    [Fact]
    public async Task CreateAsync_ExportsCachedVerificationFailureWithoutRefreshingHardware()
    {
        DiagnosticDeviceVerificationAttempt attempt = DiagnosticDeviceVerificationAttempt.FromProbe(
            Probe(BatteryValidationProbeClassification.HostIdentityBlocked) with
            {
                Stage = BatteryValidationProbeStage.HostIdentity,
                HostIdentityFailure = new HostIdentityFailureDetail(
                    HostIdentityFailureReason.RegistrySourcesMismatch,
                    "SystemInformation",
                    "BIOSVersion",
                    "4.01",
                    "3.01"),
            },
            usedRecentlyVerifiedDevice: false);
        var runtime = new SnapshotOnlyRuntime(
            CreateStatus() with { DeviceVerified = false },
            verificationDiagnostics: new DiagnosticDeviceVerification(attempt, attempt));
        var provider = new DiagnosticArchiveProvider(
            runtime,
            new DiagnosticHistory(),
            new FixedMetadataSource());

        DiagnosticArchive result = await provider.CreateAsync(true, CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        using JsonDocument verification = JsonDocument.Parse(
            await ReadEntryAsync(archive, "device-verification.json"));
        JsonElement failure = verification.RootElement.GetProperty("latestAttempt")
            .GetProperty("probeResult").GetProperty("hostIdentityFailure");
        Assert.Equal("RegistrySourcesMismatch", failure.GetProperty("reason").GetString());
        Assert.Equal("BIOSVersion", failure.GetProperty("field").GetString());
        Assert.Equal("4.01", failure.GetProperty("expectedValue").GetString());
        Assert.Equal("3.01", failure.GetProperty("actualValue").GetString());
        Assert.Equal(1, runtime.VerificationDiagnosticReads);
        Assert.Equal(0, runtime.HardwareOrMutationCalls);
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
            FullChargeOverride: false)
        {
            UnconfirmedWrites = [DeviceSubsystem.Battery],
            Firmware = new FirmwareVersions(
                "2.02",
                "1.09",
                "1.9.0.0",
                "Unavailable",
                RazerBladeModelCatalog.Rz090528ModelNumber),
        };
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

    private sealed class SnapshotOnlyRuntime(
        ServiceStatusSnapshot status,
        Rz090581CapabilityDiagnostics? capabilityDiagnostics = null,
        DiagnosticHyperBoostOwnershipJournal? hyperBoostJournalDiagnostics = null,
        DiagnosticDeviceVerification? verificationDiagnostics = null,
        DiagnosticExperimentalSupport? experimentalSupport = null)
        : IServiceControlRuntime
    {
        public int StatusReads { get; private set; }
        public int CapabilityDiagnosticReads { get; private set; }
        public int JournalDiagnosticReads { get; private set; }
        public int VerificationDiagnosticReads { get; private set; }
        public int HardwareOrMutationCalls { get; private set; }

        public ServiceStatusSnapshot GetStatus()
        {
            StatusReads++;
            return status;
        }

        public DiagnosticExperimentalSupport GetCachedExperimentalSupportDiagnostics() =>
            experimentalSupport ?? DiagnosticExperimentalSupport.NotDetected;

        public Rz090581CapabilityDiagnostics? GetCachedRz090581CapabilityDiagnostics()
        {
            CapabilityDiagnosticReads++;
            return capabilityDiagnostics;
        }

        public DiagnosticDeviceVerification GetCachedDeviceVerificationDiagnostics()
        {
            VerificationDiagnosticReads++;
            return verificationDiagnostics ?? DiagnosticDeviceVerification.NotObserved;
        }

        public Task<DiagnosticHyperBoostOwnershipJournal>
            GetHyperBoostOwnershipJournalDiagnosticsAsync(
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JournalDiagnosticReads++;
            return Task.FromResult(
                hyperBoostJournalDiagnostics
                    ?? DiagnosticHyperBoostOwnershipJournal.Missing);
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
