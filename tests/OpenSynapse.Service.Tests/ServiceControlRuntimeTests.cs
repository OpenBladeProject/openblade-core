using OpenSynapse.Contracts;
using OpenSynapse.Protocol;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class ServiceControlRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenSynapse.Runtime.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UnavailableProvider_BlocksEveryOperationWithoutDeviceCalls()
    {
        TestContext context = CreateContext(providerAvailable: false);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };

        ControlOperationResult[] results =
        [
            await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None),
            await context.Runtime.SetPerformanceOverrideAsync(
                new ManualOverride<PerformancePreset>(PerformancePreset.Performance, OverrideLifetime.Persistent),
                CancellationToken.None),
            await context.Runtime.SetFanOverrideAsync(
                new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
                CancellationToken.None),
            await context.Runtime.SetLightingOverrideAsync(null, CancellationToken.None),
            await context.Runtime.SetBatteryPolicyAsync(BatteryPolicy.Create(70), CancellationToken.None),
            await context.Runtime.SetFullChargeOverrideAsync(true, CancellationToken.None),
        ];
        await context.Runtime.RefreshAsync(CancellationToken.None);

        Assert.All(results, result => Assert.Equal(ControlOperationStatus.Blocked, result.Status));
        Assert.Contains("stored", results[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.Device.TotalCalls);
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictOrDetectionFailure_BlocksBeforeProviderAndDevice(bool detectionFailure)
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            conflicts: detectionFailure ? [] : ["RazerAppEngine"],
            detectionFailure: detectionFailure ? "enumeration failed" : null);

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        await context.Runtime.RefreshAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task UpdateProfiles_AtomicallyPersistsValidIntentWhileUnavailable()
    {
        TestContext context = CreateContext(providerAvailable: false);
        ProfileSet expected = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };

        ControlOperationResult result = await context.Runtime.UpdateProfilesAsync(
            expected,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(expected, (await context.Store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.Equal(expected, context.Runtime.GetStatus().Profiles);
    }

    [Fact]
    public async Task UpdateProfiles_RejectsInvalidIntentWithoutRevisionOrPublication()
    {
        TestContext context = CreateContext(providerAvailable: false);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet invalid = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Fans = (FanMode)999 },
        };

        ControlOperationResult result = await context.Runtime.UpdateProfilesAsync(
            invalid,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal(0, context.Runtime.GetStatus().Revision);
        Assert.Empty(context.Hub.Published);
        Assert.Equal(defaults, context.Runtime.GetStatus().Profiles);
    }

    [Fact]
    public async Task Unplug_ClearsTemporaryOverrideAndFullChargeButKeepsPersistentOverride()
    {
        TestContext context = CreateContext(providerAvailable: false);
        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
            CancellationToken.None);
        await context.Runtime.SetFullChargeOverrideAsync(true, CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.RefreshAsync(CancellationToken.None);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();

        Assert.Null(status.PerformanceOverride);
        Assert.Equal(FanMode.Maximum, status.FanOverride!.Value);
        Assert.False(status.FullChargeOverride);
        Assert.False(status.DesiredState.Battery.TemporaryFullCharge);
    }

    [Fact]
    public async Task AcceptedStateChanges_IncrementRevisionAndPublishEachSnapshot()
    {
        TestContext context = CreateContext(providerAvailable: false);

        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal([1L, 2L], context.Hub.Published.Select(status => status.Revision));
        Assert.Equal(2, context.Runtime.GetStatus().Revision);
    }

    [Fact]
    public async Task Refresh_PublishesOnlyWhenSnapshotContentChanges()
    {
        TestContext context = CreateContext(providerAvailable: false);

        await context.Runtime.RefreshAsync(CancellationToken.None);
        Assert.Empty(context.Hub.Published);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn, batteryPercent: 79);
        await context.Runtime.RefreshAsync(CancellationToken.None);

        Assert.Single(context.Hub.Published);
        Assert.Equal(1, context.Runtime.GetStatus().Revision);
    }

    [Fact]
    public async Task VerifiedDeviceSubsystemFailure_ReturnsFailedWithoutSafetyBlock()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Silent,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Device.PerformanceFailure = new InvalidOperationException("write rejected");

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Contains("write rejected", result.Message, StringComparison.Ordinal);
    }

    private TestContext CreateContext(
        bool providerAvailable,
        IReadOnlyList<string>? conflicts = null,
        string? detectionFailure = null)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var store = new ProfileStore(Path.Combine(directory, "profiles.json"));
        var telemetry = new FakeTelemetryProvider { Current = CreateTelemetry(PowerSource.PluggedIn) };
        var hub = new FakeHub();
        var device = new CountingBladeDevice();
        var provider = new CountingProvider(providerAvailable, device);
        var runtime = new ServiceControlRuntime(
            store,
            profiles,
            new FakeGuard(conflicts ?? [], detectionFailure),
            telemetry,
            hub,
            provider);
        return new TestContext(runtime, store, telemetry, hub, provider, device);
    }

    private static TelemetrySnapshot CreateTelemetry(
        PowerSource source,
        int? batteryPercent = 80) => new(
            DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
            source,
            batteryPercent,
            null,
            null,
            null);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record TestContext(
        ServiceControlRuntime Runtime,
        ProfileStore Store,
        FakeTelemetryProvider Telemetry,
        FakeHub Hub,
        CountingProvider Provider,
        CountingBladeDevice Device);

    private sealed class FakeGuard(
        IReadOnlyList<string> conflicts,
        string? detectionFailure) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new(conflicts, detectionFailure);
    }

    private sealed class FakeTelemetryProvider : IPowerTelemetryProvider
    {
        public required TelemetrySnapshot Current { get; set; }

        public TelemetrySnapshot Capture() => Current;
    }

    private sealed class FakeHub : IStatusSubscriptionHub
    {
        public List<ServiceStatusSnapshot> Published { get; } = [];

        public void Publish(ServiceStatusSnapshot snapshot) => Published.Add(snapshot);

        public StatusSubscriptionLease Subscribe(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingProvider(bool available, IBladeDevice device)
        : IVerifiedBladeDeviceProvider
    {
        public int TryGetCount { get; private set; }

        public bool TryGetVerifiedDevice(out IBladeDevice verifiedDevice)
        {
            TryGetCount++;
            verifiedDevice = device;
            return available;
        }
    }

    private sealed class CountingBladeDevice : IBladeDevice
    {
        public int TotalCalls { get; private set; }

        public DeviceCapabilities Capabilities { get; set; } =
            DeviceCapabilities.Unavailable();

        public ConfirmedDeviceState ConfirmedState { get; set; } =
            new(null, null, null, null);

        public Exception? PerformanceFailure { get; set; }

        public Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.FromResult(Capabilities);
        }

        public Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.FromResult(ConfirmedState);
        }

        public Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            if (PerformanceFailure is not null)
            {
                throw PerformanceFailure;
            }

            ConfirmedState = ConfirmedState with { Performance = value };
            return Task.CompletedTask;
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }

        public Task SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }

        public Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.CompletedTask;
        }
    }
}
