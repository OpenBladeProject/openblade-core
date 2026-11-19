using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadControlRuntimeTests
{
    [Fact]
    public async Task StartupAndSavedIntentDoNotOpenOrWriteHardware()
    {
        var provider = new Provider();
        var store = new CoolingPadStateStore();
        await store.SaveSettingsAsync("owner", Fixed, CancellationToken.None);
        var runtime = new CoolingPadControlRuntime(provider, store, new Temperatures());
        await runtime.SetUserAsync("owner", CancellationToken.None);
        Assert.Equal(CoolingPadFanMode.Fixed, runtime.Status.Requested.FanMode);
        Assert.False(runtime.Status.Active);
        await runtime.MaintainAsync(() => true, CancellationToken.None);
        Assert.Equal(0, provider.Opens);
    }

    [Fact]
    public async Task DirectFanTransitionRestoresExactSavedVendorPresetWithoutLightingReadback()
    {
        var provider = new Provider();
        provider.Pad.Snapshot = provider.Pad.Snapshot with { LightingConfiguration = null, BrightnessRaw = null };
        CoolingPadDeviceSnapshot baseline = provider.Pad.Snapshot;
        var runtime = await CreateAsync(provider);
        Assert.Equal(ControlOperationStatus.Applied, (await runtime.ApplyAsync(Fixed, () => true, default)).Status);
        Assert.Equal(["fan:Fixed:1500"], provider.Pad.Writes);
        Assert.Null(runtime.Status.MeasuredRpm);
        Assert.Equal(1500, runtime.Status.ConfirmedTargetRpm);
        Assert.Equal(ControlOperationStatus.Applied, (await runtime.ReleaseAsync(() => true, default)).Status);
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(provider.Pad.Restored));
        Assert.True(provider.Pad.RestoredFan);
        Assert.False(provider.Pad.RestoredLighting);
        Assert.False(runtime.Status.RecoveryRequired);
    }

    [Fact]
    public async Task AmbiguousWriteStopsMaintenanceAndCleanupUntilExplicitRead()
    {
        var provider = new Provider();
        provider.Pad.FailWrite = true;
        var runtime = await CreateAsync(provider);
        Assert.Equal(ControlOperationStatus.Failed, (await runtime.ApplyAsync(Fixed, () => true, default)).Status);
        Assert.True(runtime.Status.WritesPaused);
        await runtime.MaintainAsync(() => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, (await runtime.ReleaseAsync(() => true, default)).Status);
        Assert.Single(provider.Pad.Writes);
        Assert.Null(provider.Pad.Restored);
        provider.Pad.FailWrite = false;
        Assert.Equal(ControlOperationStatus.Stored, (await runtime.ReadAsync(default)).Status);
        Assert.False(runtime.Status.WritesPaused);
        Assert.True(runtime.Status.RecoveryRequired);
        Assert.Equal(ControlOperationStatus.Applied, (await runtime.ReleaseAsync(() => true, default)).Status);
        Assert.NotNull(provider.Pad.Restored);
    }

    [Fact]
    public async Task CrashRetainsExactBaselineButDoesNotReplayControl()
    {
        var provider = new Provider();
        var store = new CoolingPadStateStore();
        var first = await CreateAsync(provider, store);
        await first.ApplyAsync(Fixed, () => true, default);
        int writes = provider.Pad.Writes.Count;
        var restarted = await CreateAsync(provider, store);
        Assert.True(restarted.Status.RecoveryRequired);
        Assert.True(restarted.Status.WritesPaused);
        await restarted.MaintainAsync(() => true, default);
        Assert.Equal(writes, provider.Pad.Writes.Count);
        Assert.Equal(ControlOperationStatus.Blocked, (await restarted.ApplyAsync(Fixed, () => true, default)).Status);
        await restarted.ReadAsync(default);
        await restarted.ReleaseAsync(() => true, default);
        Assert.Equal(CoolingPadFanPreset.Quiet, provider.Pad.Restored!.FanConfiguration!.Preset);
    }

    [Fact]
    public async Task MissingTelemetryPausesWithoutInventingATarget()
    {
        var provider = new Provider();
        var store = new CoolingPadStateStore();
        var runtime = await CreateAsync(provider, store, new Temperatures { Reading = new(65, null) });
        ControlOperationResult result = await runtime.ApplyAsync(Fixed with { FanMode = CoolingPadFanMode.HostCurve }, () => true, default);
        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Empty(provider.Pad.Writes);
        Assert.False(runtime.Status.Active);
        Assert.Null(await store.LoadOwnershipAsync(default));
    }

    [Fact]
    public async Task RevocationDuringSubmissionCannotRearmMaintenance()
    {
        var provider = new Provider();
        var runtime = await CreateAsync(provider);
        provider.Pad.AfterFan = runtime.Revoke;
        await runtime.ApplyAsync(Fixed, () => true, default);
        Assert.False(runtime.Status.Active);
        Assert.True(runtime.Status.RecoveryRequired);
        await runtime.MaintainAsync(() => true, default);
        Assert.Single(provider.Pad.Writes);
    }

    [Fact]
    public async Task NativeSubmissionRechecksAdmissionAfterBaselineRead()
    {
        var provider = new Provider();
        var runtime = await CreateAsync(provider);
        bool admitted = true;
        provider.Pad.BeforeFan = () => admitted = false;
        Assert.Equal(ControlOperationStatus.Failed, (await runtime.ApplyAsync(Fixed, () => admitted, default)).Status);
        Assert.Empty(provider.Pad.Writes);
        Assert.False(runtime.Status.WritesPaused);
    }

    [Fact]
    public async Task LeasesAreBoundedOwnerBoundAndExpireToConfiguredTarget()
    {
        var provider = new Provider();
        var clock = new Clock();
        var runtime = new CoolingPadControlRuntime(provider, new(), new Temperatures(), clock);
        await runtime.SetUserAsync("owner", default);
        await runtime.ApplyAsync(Fixed with { AllowDemandApi = true }, () => true, default);
        Guid id = Guid.NewGuid();
        Assert.Equal(ControlOperationStatus.Stored, (await runtime.SetLeaseAsync(id, "client-a", 2500, 2, () => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await runtime.SetLeaseAsync(id, "client-b", 3000, 2, () => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await runtime.RemoveLeaseAsync(id, "client-b", default)).Status);
        for (int i = 0; i < 7; i++) { await runtime.SetLeaseAsync(Guid.NewGuid(), "client-a", 2000, 2, () => true, default); }
        Assert.Equal(ControlOperationStatus.Blocked, (await runtime.SetLeaseAsync(Guid.NewGuid(), "client-a", 2000, 2, () => true, default)).Status);
        await runtime.MaintainAsync(() => true, default);
        Assert.Equal(2500, runtime.Status.ConfirmedTargetRpm);
        clock.Now = clock.Now.AddSeconds(3);
        await runtime.MaintainAsync(() => true, default);
        Assert.Equal(1500, runtime.Status.ConfirmedTargetRpm);
        await runtime.ReleaseAsync(() => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, (await runtime.SetLeaseAsync(id, "client-a", 2500, 2, () => true, default)).Status);
    }

    [Fact]
    public async Task DifferentUserCannotOverwriteOrRestoreRetainedOwnership()
    {
        var provider = new Provider();
        var runtime = await CreateAsync(provider);
        await runtime.ApplyAsync(Fixed, () => true, default);
        await runtime.SetUserAsync("other-owner", default);
        await runtime.ReadAsync(default);
        Assert.Equal(ControlOperationStatus.Blocked, (await runtime.ReleaseAsync(() => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await runtime.ApplyAsync(Fixed, () => true, default)).Status);
        Assert.Null(provider.Pad.Restored);
    }

    [Fact]
    public async Task AtomicStoreRoundTripsOpaqueBaselineAndOwnerSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.CoolingPadTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CoolingPadStateStore(directory, _ => { });
            var record = new CoolingPadOwnershipRecord("owner", new Pad().Snapshot, true, true);
            await store.SaveOwnershipAsync(record, default);
            await store.SaveSettingsAsync("owner", Fixed, default);
            var reloaded = new CoolingPadStateStore(directory, _ => { });
            CoolingPadOwnershipRecord read = Assert.IsType<CoolingPadOwnershipRecord>(await reloaded.LoadOwnershipAsync(default));
            Assert.Equal(record.Owner, read.Owner);
            Assert.Equal(record.Baseline.FanConfiguration, read.Baseline.FanConfiguration);
            Assert.Equal(record.Baseline.BrightnessRaw, read.Baseline.BrightnessRaw);
            Assert.Equal(record.Baseline.LightingConfiguration!.Colors, read.Baseline.LightingConfiguration!.Colors);
            Assert.Equal(CoolingPadFanMode.Fixed, (await reloaded.LoadSettingsAsync("owner", default)).FanMode);
            Assert.Equal(CoolingPadFanMode.Keep, (await reloaded.LoadSettingsAsync("other", default)).FanMode);
            await reloaded.ClearOwnershipAsync(default);
            Assert.Null(await new CoolingPadStateStore(directory, _ => { }).LoadOwnershipAsync(default));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"SchemaVersion\":1}")]
    [InlineData("{broken")]
    public async Task CorruptDuplicateAndFutureJournalNeverPermitWrites(string json)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.CoolingPadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "ownership.json"), json);
            var provider = new Provider();
            var runtime = new CoolingPadControlRuntime(provider, new CoolingPadStateStore(directory, _ => { }), new Temperatures());
            await runtime.SetUserAsync("owner", default);
            Assert.True(runtime.Status.WritesPaused);
            Assert.False(runtime.Status.Active);
            Assert.Equal(ControlOperationStatus.Failed, (await runtime.ApplyAsync(Fixed, () => true, default)).Status);
            Assert.Empty(provider.Pad.Writes);
            Assert.Equal(json, await File.ReadAllTextAsync(Path.Combine(directory, "ownership.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MetadataDiscoveryPreservesConnectedCardThroughUnavailableObservation()
    {
        var provider = new Provider { Connection = CoolingPadConnectionState.PresentExact };
        var clock = new Clock();
        var runtime = new CoolingPadControlRuntime(provider, new(), new Temperatures(), clock);
        await runtime.MaintainAsync(() => true, default);
        Assert.True(runtime.Status.Connected);
        Assert.Equal(0, provider.Opens);
        provider.Connection = CoolingPadConnectionState.Unavailable;
        clock.Now = clock.Now.AddSeconds(3);
        await runtime.MaintainAsync(() => true, default);
        Assert.True(runtime.Status.Connected);
        provider.Connection = CoolingPadConnectionState.Absent;
        clock.Now = clock.Now.AddSeconds(3);
        await runtime.MaintainAsync(() => true, default);
        Assert.False(runtime.Status.Connected);
    }

    [Fact]
    public async Task CpuOnlyCurveAcceptsMissingGpuWithoutInventingGpuTemperature()
    {
        var provider = new Provider();
        var runtime = await CreateAsync(provider, temperatures: new Temperatures { Reading = new(65, null) });
        var settings = Fixed with { FanMode = CoolingPadFanMode.HostCurve, TemperatureSource = CoolingPadTemperatureSource.Cpu };
        Assert.Equal(ControlOperationStatus.Applied, (await runtime.ApplyAsync(settings, () => true, default)).Status);
        Assert.Equal(1500, runtime.Status.ConfirmedTargetRpm);
    }

    [Theory]
    [InlineData(CoolingPadTemperatureSource.Cpu, true, false)]
    [InlineData(CoolingPadTemperatureSource.Gpu, false, true)]
    [InlineData(CoolingPadTemperatureSource.Both, true, true)]
    public async Task HostCurveRequestsCoolingSamplesForOnlyItsSelectedSensors(
        CoolingPadTemperatureSource selection, bool cpu, bool gpu)
    {
        var temperatures = new Temperatures();
        var runtime = await CreateAsync(new Provider(), temperatures: temperatures);
        var settings = Fixed with { FanMode = CoolingPadFanMode.HostCurve, TemperatureSource = selection };

        Assert.Equal(ControlOperationStatus.Applied, (await runtime.ApplyAsync(settings, () => true, default)).Status);
        Assert.Equal((cpu, gpu), temperatures.LastCurveSelection);
        Assert.Equal(0, temperatures.BackgroundReads);
    }

    [Fact]
    public async Task StaleTemperatureCannotReachNativeSubmission()
    {
        var provider = new Provider();
        var clock = new Clock();
        var runtime = new CoolingPadControlRuntime(provider, new(), new Temperatures(), clock);
        await runtime.SetUserAsync("owner", default);
        provider.Pad.BeforeFan = () => clock.Now = clock.Now.AddSeconds(6);
        var settings = Fixed with { FanMode = CoolingPadFanMode.HostCurve };
        Assert.Equal(ControlOperationStatus.Failed, (await runtime.ApplyAsync(settings, () => true, default)).Status);
        Assert.Empty(provider.Pad.Writes);
    }

    [Fact]
    public async Task CleanPauseAndKeepAllowDirectNewDestinationWithOriginalBaseline()
    {
        var provider = new Provider();
        var runtime = await CreateAsync(provider);
        await runtime.ApplyAsync(Fixed, () => true, default);
        runtime.Revoke();
        await runtime.ApplyAsync(Fixed with { FixedRpm = 2000 }, () => true, default);
        Assert.Null(provider.Pad.Restored);
        Assert.Equal(2000, runtime.Status.ConfirmedTargetRpm);
        await runtime.ApplyAsync(new CoolingPadSettings(), () => true, default);
        Assert.False(runtime.Status.Active);
        Assert.True(runtime.Status.RecoveryRequired);
        await runtime.ApplyAsync(Fixed with { FixedRpm = 2500 }, () => true, default);
        Assert.Null(provider.Pad.Restored);
        await runtime.ReleaseAsync(() => true, default);
        Assert.Equal(1000, provider.Pad.Restored!.FanConfiguration!.ConfiguredRpm);
    }

    [Fact]
    public async Task MaintenanceRechecksTemperatureAgeAtNativeSubmission()
    {
        var provider = new Provider();
        var clock = new Clock();
        var temperatures = new Temperatures();
        var runtime = new CoolingPadControlRuntime(provider, new(), temperatures, clock);
        await runtime.SetUserAsync("owner", default);
        await runtime.ApplyAsync(Fixed with { FanMode = CoolingPadFanMode.HostCurve }, () => true, default);
        temperatures.Reading = new(80, 70);
        clock.Now = clock.Now.AddSeconds(2);
        provider.Pad.BeforeFan = () => clock.Now = clock.Now.AddSeconds(6);
        await runtime.MaintainAsync(() => true, default);
        Assert.Single(provider.Pad.Writes);
        Assert.False(runtime.Status.Active);
        Assert.Equal(1500, runtime.Status.ConfirmedTargetRpm);
    }

    [Fact]
    public async Task UnavailableControlPortPreservesConfirmedMetadataPresence()
    {
        var provider = new Provider { Connection = CoolingPadConnectionState.PresentExact, ReturnNull = true };
        var runtime = await CreateAsync(provider);
        await runtime.MaintainAsync(() => true, default);
        Assert.True(runtime.Status.Connected);
        Assert.Equal(ControlOperationStatus.Failed, (await runtime.ReadAsync(default)).Status);
        Assert.True(runtime.Status.Connected);
        Assert.Contains("unavailable", runtime.Status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Review_ActiveFixedControlDetectsConfirmedUnplugWithoutCleanupWrites()
    {
        var provider = new Provider { Connection = CoolingPadConnectionState.PresentExact };
        var clock = new Clock();
        var runtime = new CoolingPadControlRuntime(provider, new(), new Temperatures(), clock);
        await runtime.SetUserAsync("owner", default);
        await runtime.ApplyAsync(Fixed, () => true, default);
        provider.Connection = CoolingPadConnectionState.Absent;
        clock.Now = clock.Now.AddSeconds(3);

        await runtime.MaintainAsync(() => true, default);

        Assert.False(runtime.Status.Active);
        Assert.False(runtime.Status.Connected);
        Assert.Equal(CoolingPadConnectionState.Absent, runtime.Status.Connection);
        Assert.Single(provider.Pad.Writes);
        Assert.Null(provider.Pad.Restored);
        Assert.Equal(1, provider.Pad.DisposeCalls);
    }

    [Fact]
    public async Task Review_QueuedInactiveDiscoveryRechecksStateAfterApplyWinsGate()
    {
        var provider = new Provider { Connection = CoolingPadConnectionState.Absent };
        var runtime = await CreateAsync(provider);
        var operationGate = Assert.IsType<SemaphoreSlim>(typeof(CoolingPadControlRuntime)
            .GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(runtime));
        await operationGate.WaitAsync();
        Task<ControlOperationResult> apply;
        Task maintenance;
        try
        {
            apply = runtime.ApplyAsync(Fixed, () => true, default);
            maintenance = runtime.MaintainAsync(() => true, default);
        }
        finally { operationGate.Release(); }
        await Task.WhenAll(apply, maintenance).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(runtime.Status.Active);
        Assert.False(runtime.Status.Connected);
        Assert.Equal(CoolingPadConnectionState.Absent, runtime.Status.Connection);
        Assert.Equal(1, provider.Pad.DisposeCalls);
        Assert.Single(provider.Pad.Writes);
    }

    [Fact]
    public async Task Review_UnknownHandoffRequiresExplicitReadBeforeServiceBaselineRecovery()
    {
        var provider = new Provider();
        var runtime = await CreateAsync(provider);
        provider.Pad.FailWrite = true;
        await runtime.ApplyAsync(Fixed, () => true, default);
        int reads = provider.Pad.ReadCalls;
        Assert.Equal(ControlOperationStatus.Blocked,
            (await runtime.HandBackForUserTransitionAsync(() => true, default)).Status);
        Assert.Equal(reads, provider.Pad.ReadCalls);
        Assert.Null(provider.Pad.Restored);

        await runtime.SetUserAsync("other-owner", default);
        provider.Pad.FailWrite = false;
        await runtime.ReadAsync(default);
        Assert.Equal(CoolingPadFanMode.Keep, runtime.Status.Requested.FanMode);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await runtime.ApplyAsync(Fixed with { FixedRpm = 2000 }, () => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Applied,
            (await runtime.ReleaseAsync(() => true, default, serviceOwnedRestoration: true)).Status);
        Assert.Equal(1000, provider.Pad.Restored!.FanConfiguration!.ConfiguredRpm);
        Assert.Equal(ControlOperationStatus.Applied,
            (await runtime.ApplyAsync(Fixed with { FixedRpm = 2000 }, () => true, default)).Status);
        Assert.Equal(new[] { "fan:Fixed:1500", "fan:Fixed:2000" }, provider.Pad.Writes);
    }

    [Fact]
    public async Task Review_TransientSubmissionEpochDefersTickWithoutLosingContinuedOwnership()
    {
        var provider = new Provider();
        var clock = new Clock();
        var temperatures = new Temperatures();
        var runtime = new CoolingPadControlRuntime(provider, new(), temperatures, clock);
        await runtime.SetUserAsync("owner", default);
        bool initialSubmissionEpoch = true;
        await runtime.ApplyAsync(Fixed with { FanMode = CoolingPadFanMode.HostCurve },
            () => initialSubmissionEpoch, default, continuedOwnership: () => true);
        initialSubmissionEpoch = false;
        temperatures.Reading = new(80, 70);
        clock.Now = clock.Now.AddSeconds(2);

        await runtime.MaintainAsync(() => false, default);

        Assert.True(runtime.Status.Active);
        Assert.Single(provider.Pad.Writes);
        await runtime.MaintainAsync(() => true, default);
        Assert.True(runtime.Status.Active);
        Assert.Equal(2500, runtime.Status.ConfirmedTargetRpm);
        Assert.Equal(2, provider.Pad.Writes.Count);
    }

    [Fact]
    public async Task Review_DemandApiDefaultsOffAndOptingOutClearsExistingLeases()
    {
        var provider = new Provider();
        var clock = new Clock();
        var runtime = new CoolingPadControlRuntime(provider, new(), new Temperatures(), clock);
        await runtime.SetUserAsync("owner", default);
        await runtime.ApplyAsync(Fixed, () => true, default);
        Guid lease = Guid.NewGuid();
        Assert.Equal(ControlOperationStatus.Blocked,
            (await runtime.SetLeaseAsync(lease, "client", 2500, 30, () => true, default)).Status);
        await runtime.ApplyAsync(Fixed with { AllowDemandApi = true }, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored,
            (await runtime.SetLeaseAsync(lease, "client", 2500, 30, () => true, default)).Status);
        await runtime.MaintainAsync(() => true, default);
        Assert.Equal(2500, runtime.Status.ConfirmedTargetRpm);

        await runtime.ApplyAsync(Fixed with { AllowDemandApi = false }, () => true, default);

        Assert.Equal(1500, runtime.Status.ConfirmedTargetRpm);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await runtime.SetLeaseAsync(lease, "client", 2500, 30, () => true, default)).Status);
        await runtime.ApplyAsync(Fixed with { AllowDemandApi = true }, () => true, default);
        clock.Now = clock.Now.AddSeconds(2);
        await runtime.MaintainAsync(() => true, default);
        Assert.Equal(1500, runtime.Status.ConfirmedTargetRpm);
    }

    [Fact]
    public async Task Review_UntrustedStorageErrorMessageRemainsWithinStatusContract()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.CoolingPadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string json = "{\"schemaVersion\":1,\"" + new string('x', 4096) + "\":0}";
            await File.WriteAllTextAsync(Path.Combine(directory, "ownership.json"), json);
            var runtime = new CoolingPadControlRuntime(new Provider(),
                new CoolingPadStateStore(directory, _ => { }), new Temperatures());

            await runtime.SetUserAsync("owner", default);
            Assert.True(runtime.Status.WritesPaused);
            Assert.InRange(runtime.Status.Message.Length, 1, 2048);
            Assert.Equal(ControlOperationStatus.Failed, (await runtime.ReadAsync(default)).Status);
            Assert.InRange(runtime.Status.Message.Length, 1, 2048);
            Assert.Equal(json, await File.ReadAllTextAsync(Path.Combine(directory, "ownership.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Review_RevocationDuringAuthorityCheckCannotAdoptTheNewGeneration()
    {
        var provider = new Provider();
        var clock = new Clock();
        var temperatures = new Temperatures();
        var runtime = new CoolingPadControlRuntime(provider, new(), temperatures, clock);
        await runtime.SetUserAsync("owner", default);
        bool revokeDuringCheck = false;
        bool Owns()
        {
            if (revokeDuringCheck) { revokeDuringCheck = false; runtime.Revoke(); }
            return true;
        }
        await runtime.ApplyAsync(Fixed with { FanMode = CoolingPadFanMode.HostCurve },
            () => true, default, continuedOwnership: Owns);
        temperatures.Reading = new(80, 70);
        clock.Now = clock.Now.AddSeconds(2);
        revokeDuringCheck = true;

        await runtime.MaintainAsync(() => true, default);

        Assert.False(runtime.Status.Active);
        Assert.Single(provider.Pad.Writes);
    }

    [Fact]
    public async Task Review_OptionalStorageValidationFailureIsContainedButCancellationRemainsObservable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.CoolingPadTests", Guid.NewGuid().ToString("N"));
        var provider = new Provider();
        var invalid = new CoolingPadControlRuntime(provider,
            new CoolingPadStateStore(directory, _ => throw new FormatException("Synthetic optional validation failure.")),
            new Temperatures());
        await invalid.SetUserAsync("owner", default);
        Assert.True(invalid.Status.WritesPaused);
        Assert.False(invalid.Status.Active);
        Assert.Equal(0, provider.Opens);

        using var cancellation = new CancellationTokenSource();
        var canceled = new CoolingPadControlRuntime(provider, new CoolingPadStateStore(directory, _ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }), new Temperatures());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.SetUserAsync("owner", cancellation.Token));
        Assert.Equal(0, provider.Opens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Review_LateProvenLocalDeferralPreservesAuthorityButControllerRefusalStops(bool localDeferral)
    {
        var provider = new Provider();
        var clock = new Clock();
        var temperatures = new Temperatures();
        var runtime = new CoolingPadControlRuntime(provider, new(), temperatures, clock);
        await runtime.SetUserAsync("owner", default);
        await runtime.ApplyAsync(Fixed with { FanMode = CoolingPadFanMode.HostCurve },
            () => true, default, continuedOwnership: () => true);
        temperatures.Reading = new(80, 70);
        clock.Now = clock.Now.AddSeconds(2);
        bool admitted = true;
        provider.Pad.MarkLocalAdmissionDeferral = localDeferral;
        provider.Pad.BeforeFan = () => admitted = false;

        await runtime.MaintainAsync(() => admitted, default);

        Assert.Equal(localDeferral, runtime.Status.Active);
        Assert.Single(provider.Pad.Writes);
        Assert.False(runtime.Status.WritesPaused);
        Assert.Equal(1500, runtime.Status.ConfirmedTargetRpm);
        provider.Pad.BeforeFan = null;
        admitted = true;
        clock.Now = clock.Now.AddSeconds(2);
        await runtime.MaintainAsync(() => admitted, default);
        Assert.Equal(localDeferral ? 2 : 1, provider.Pad.Writes.Count);
        Assert.Equal(localDeferral ? 2500 : 1500, runtime.Status.ConfirmedTargetRpm);
    }

    private static CoolingPadSettings Fixed => new() { FanMode = CoolingPadFanMode.Fixed, FixedRpm = 1500 };
    private static async Task<CoolingPadControlRuntime> CreateAsync(Provider provider,
        CoolingPadStateStore? store = null, Temperatures? temperatures = null)
    {
        var runtime = new CoolingPadControlRuntime(provider, store ?? new(), temperatures ?? new());
        await runtime.SetUserAsync("owner", default);
        return runtime;
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private sealed class Temperatures : IFanTemperatureSource
    {
        internal FanTemperatureReadings Reading = new(65, 60);
        internal (bool Cpu, bool Gpu)? LastCurveSelection;
        internal int BackgroundReads;
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token)
        {
            BackgroundReads++;
            return ValueTask.FromResult(Reading);
        }
        public ValueTask<FanTemperatureReadings> ReadForFanCurveAsync(bool includeCpu, bool includeGpu, CancellationToken token)
        {
            LastCurveSelection = (includeCpu, includeGpu);
            return ValueTask.FromResult(new FanTemperatureReadings(
                includeCpu ? Reading.CpuCelsius : null,
                includeGpu ? Reading.GpuCelsius : null,
                includeGpu && Reading.GpuInactive,
                includeGpu && Reading.GpuPoweredDown));
        }
    }
    private sealed class Provider : ICoolingPadDeviceProvider
    {
        internal readonly Pad Pad = new();
        internal int Opens;
        internal bool ReturnNull;
        internal CoolingPadConnectionState Connection = CoolingPadConnectionState.Unavailable;
        public CoolingPadConnectionState ObserveConnection() => Connection;
        public Task<ICoolingPadDevice?> OpenAsync(CancellationToken token) { Opens++; return Task.FromResult<ICoolingPadDevice?>(ReturnNull ? null : Pad); }
    }
    private sealed class Pad : ICoolingPadDevice
    {
        public string Identity => "exact-pad-hash";
        internal CoolingPadDeviceSnapshot Snapshot = new("exact-pad-hash", null, 1000, 50, CoolingPadLightingMode.Static)
        {
            FanConfiguration = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Quiet, 1000),
            LightingConfiguration = new(1, 0, 1, 0, 0, [new(1, 2, 3)]),
            ActiveLightingConfiguration = new(0, 0, 1, 0, 0, [new(1, 2, 3)]),
            BrightnessRaw = 127,
            DeviceMode = CoolingPadDeviceMode.Normal,
        };
        internal List<string> Writes = [];
        internal bool FailWrite;
        internal bool MarkLocalAdmissionDeferral;
        internal int DisposeCalls;
        internal int ReadCalls;
        internal Action? BeforeFan, AfterFan;
        internal CoolingPadDeviceSnapshot? Restored;
        internal bool RestoredFan, RestoredLighting;
        public Task<CoolingPadDeviceSnapshot> ReadSnapshotAsync(CancellationToken token) { ReadCalls++; return Task.FromResult(Snapshot); }
        public Task SetFanAsync(CoolingPadFanMode mode, int? rpm, Func<bool> allowed, CancellationToken token)
        {
            BeforeFan?.Invoke();
            if (!allowed()) { throw new CoolingPadOperationException("Admission revoked", false) { LocalAdmissionDeferred = MarkLocalAdmissionDeferral }; }
            Writes.Add($"fan:{mode}:{rpm}");
            if (FailWrite) { throw new CoolingPadOperationException("Ambiguous submission", true); }
            Snapshot = Snapshot with { FanMode = mode, TargetRpm = rpm };
            AfterFan?.Invoke();
            return Task.CompletedTask;
        }
        public Task SetLightingAsync(CoolingPadSettings settings, Func<bool> allowed, CancellationToken token)
        {
            if (!allowed()) { throw new CoolingPadOperationException("Admission revoked", false); }
            Writes.Add("lighting");
            Snapshot = Snapshot with { LightingMode = settings.LightingMode, BrightnessPercent = settings.BrightnessPercent };
            return Task.CompletedTask;
        }
        public Task PushMirrorFrameAsync(IReadOnlyList<CoolingPadColor> colors, Func<bool> allowed, CancellationToken token) => Task.CompletedTask;
        public Task RestoreAsync(CoolingPadDeviceSnapshot snapshot, bool fan, bool lighting, Func<bool> allowed, CancellationToken token)
        {
            if (!allowed()) { throw new CoolingPadOperationException("Admission revoked", false); }
            Restored = snapshot; RestoredFan = fan; RestoredLighting = lighting; Snapshot = snapshot;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
