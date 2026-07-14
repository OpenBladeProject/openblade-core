using OpenSynapse.Contracts;
using OpenSynapse.Protocol;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class ControlRequestHandlerTests
{
    [Fact]
    public async Task HandleAsync_DispatchesEveryClosedRequestToExactlyOneRuntimeMethod()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        ServiceRequest[] requests =
        [
            new GetStatusRequest(2, Guid.NewGuid()),
            new SubscribeStatusRequest(2, Guid.NewGuid()),
            new UpdateProfilesRequest(2, Guid.NewGuid(), profiles),
            new SetPerformanceOverrideRequest(2, Guid.NewGuid(),
                new ManualOverride<PerformancePreset>(PerformancePreset.Silent, OverrideLifetime.Persistent)),
            new SetFanOverrideRequest(2, Guid.NewGuid(),
                new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.UntilPowerSourceChange)),
            new SetLightingOverrideRequest(2, Guid.NewGuid(), null),
            new SetBatteryPolicyRequest(2, Guid.NewGuid(), BatteryPolicy.Create(75)),
            new SetFullChargeOverrideRequest(2, Guid.NewGuid(), true),
        ];
        string[] expectedCalls =
        [
            "GetStatus", "GetStatus", "UpdateProfiles", "SetPerformanceOverride",
            "SetFanOverride", "SetLightingOverride", "SetBatteryPolicy",
            "SetFullChargeOverride",
        ];

        for (int index = 0; index < requests.Length; index++)
        {
            var runtime = new RecordingRuntime();
            var handler = new ControlRequestHandler(runtime);

            ServiceResponse response = await handler.HandleAsync(
                requests[index],
                CancellationToken.None);

            Assert.Equal(OpenSynapseIpc.ProtocolVersion, response.Version);
            Assert.Equal(requests[index].RequestId, response.RequestId);
            Assert.True(response.Success);
            Assert.Equal([expectedCalls[index]], runtime.Calls);
            if (requests[index] is GetStatusRequest or SubscribeStatusRequest)
            {
                Assert.NotNull(response.Status);
                Assert.Null(response.Result);
            }
            else
            {
                Assert.Null(response.Status);
                Assert.NotNull(response.Result);
            }
        }
    }

    [Theory]
    [InlineData(1, "23c09fe2-cbb8-4b74-a175-78dfb7831cf1")]
    [InlineData(2, "00000000-0000-0000-0000-000000000000")]
    public async Task HandleAsync_RejectsInvalidEnvelopeWithoutRuntimeDispatch(
        int version,
        string requestId)
    {
        var runtime = new RecordingRuntime();
        var handler = new ControlRequestHandler(runtime);
        var request = new GetStatusRequest(version, Guid.Parse(requestId));

        ServiceResponse response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal(OpenSynapseIpc.ProtocolVersion, response.Version);
        Assert.Empty(runtime.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task HandleAsync_UnavailableOrConflictedRuntime_PerformsNoDeviceCalls(
        bool providerAvailable,
        bool conflict)
    {
        string path = Path.Combine(Path.GetTempPath(), $"OpenSynapse.Handler.{Guid.NewGuid():N}.json");
        try
        {
            var device = new CountingDevice();
            var provider = new CountingProvider(providerAvailable, device);
            ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
            using var runtime = new ServiceControlRuntime(
                new ProfileStore(path),
                profiles,
                new FixedGuard(conflict ? ["RazerAppEngine"] : []),
                new FixedTelemetry(),
                new NullHub(),
                provider);
            var handler = new ControlRequestHandler(runtime);

            ServiceResponse response = await handler.HandleAsync(
                new SetFullChargeOverrideRequest(2, Guid.NewGuid(), true),
                CancellationToken.None);

            Assert.True(response.Success);
            Assert.Equal(ControlOperationStatus.Blocked, response.Result!.Status);
            Assert.Equal(0, device.Calls);
            Assert.Equal(conflict ? 0 : 1, provider.Calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class RecordingRuntime : IServiceControlRuntime
    {
        public List<string> Calls { get; } = [];

        public ServiceStatusSnapshot GetStatus()
        {
            Calls.Add("GetStatus");
            ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
            return new ServiceStatusSnapshot(
                0, false, true, [], "Unavailable", DeviceCapabilities.Unavailable(), null,
                profiles,
                new DesiredDeviceState(
                    new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                    new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                    new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                    profiles.BatteryPolicy),
                new TelemetrySnapshot(DateTimeOffset.UnixEpoch, PowerSource.PluggedIn, null, null, null, null),
                null, null, null, false);
        }

        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) =>
            Record("UpdateProfiles");

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) =>
            Record("SetPerformanceOverride");

        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) =>
            Record("SetFanOverride");

        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) =>
            Record("SetLightingOverride");

        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) =>
            Record("SetBatteryPolicy");

        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) =>
            Record("SetFullChargeOverride");

        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;

        private Task<ControlOperationResult> Record(string name)
        {
            Calls.Add(name);
            return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Stored, "Stored"));
        }
    }

    private sealed class FixedGuard(IReadOnlyList<string> conflicts) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new(conflicts, null);
    }

    private sealed class FixedTelemetry : IPowerTelemetryProvider
    {
        public TelemetrySnapshot Capture() => new(
            DateTimeOffset.UnixEpoch,
            PowerSource.PluggedIn,
            80,
            null,
            null,
            null);
    }

    private sealed class NullHub : IStatusSubscriptionHub
    {
        public StatusSubscriptionLease Subscribe(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public void Publish(ServiceStatusSnapshot snapshot) { }
    }

    private sealed class CountingProvider(bool available, IBladeDevice device)
        : IVerifiedBladeDeviceProvider
    {
        public int Calls { get; private set; }
        public bool TryGetVerifiedDevice(out IBladeDevice verified)
        {
            Calls++;
            verified = device;
            return available;
        }
    }

    private sealed class CountingDevice : IBladeDevice
    {
        public int Calls { get; private set; }
        public Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken) => Count(DeviceCapabilities.Unavailable());
        public Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken) => Count(new ConfirmedDeviceState(null, null, null, null));
        public Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken) => Count();
        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken) => Count();
        public Task SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken cancellationToken) => Count();
        public Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken) => Count();
        private Task<T> Count<T>(T value) { Calls++; return Task.FromResult(value); }
        private Task Count() { Calls++; return Task.CompletedTask; }
    }
}
