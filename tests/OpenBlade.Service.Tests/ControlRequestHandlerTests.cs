using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ControlRequestHandlerTests
{
    [Fact]
    public async Task HandleAsync_DispatchesEveryClosedRequestToExactlyOneRuntimeMethod()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        ServiceRequest[] requests =
        [
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), profiles),
            new SetPerformanceOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
                new ManualOverride<PerformancePreset>(PerformancePreset.Silent, OverrideLifetime.Persistent)),
            new CyclePerformanceShortcutRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                PerformanceShortcutSource.FunctionP),
            new SetFanOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(),
                new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.UntilPowerSourceChange)),
            new SetLightingOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), null),
            new StepKeyboardBrightnessRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                KeyboardBrightnessDirection.Increase,
                OverrideLifetime.UntilPowerSourceChange),
            new SetExternalLightingOverridePolicyRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                Enabled: true),
            new SetLightingSuppressionRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                LightingSuppressionReason.DisplayOff,
                Suppressed: true),
            new PulseReactiveKeyRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                new ReactiveKeyPulse(77)),
            new PushAmbientLightingFrameRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                new AmbientLightingFrame(
                    new RgbColor[AmbientLightingFrame.SlotCount]).ToRgbBytes()),
            new SetBatteryPolicyRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                BatteryPolicy.Create(75)),
            new SetBatteryProtectionRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                Enabled: false),
            new SetFullChargeOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), true),
            new ApplyOneShotLaptopControlRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.FunctionKeyPrimary,
                    FunctionKeyMode: FunctionKeyPrimaryMode.FunctionKeys)),
            new ExportDiagnosticsRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                LiveReadingsEnabled: true),
            new ShutdownOpenBladeRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
        ];
        string[] expectedCalls =
        [
            "GetStatus", "GetStatus", "UpdateProfiles", "SetPerformanceOverride",
            "CyclePerformanceShortcut", "SetFanOverride", "SetLightingOverride",
            "StepKeyboardBrightness",
            "SetExternalLightingOverridePolicy", "SetLightingSuppression",
            "PulseReactiveKey", "PushAmbientLightingFrame", "SetBatteryPolicy",
            "SetBatteryProtection", "SetFullChargeOverride", "ApplyOneShotLaptopControl",
            "ExportDiagnostics", "Shutdown",
        ];

        for (int index = 0; index < requests.Length; index++)
        {
            var runtime = new RecordingRuntime();
            var archives = new RecordingArchiveProvider();
            var shutdown = new RecordingShutdownSignal();
            var handler = new ControlRequestHandler(runtime, archives, shutdown);

            ServiceResponse response = await handler.HandleAsync(
                requests[index],
                CancellationToken.None);

            Assert.Equal(OpenBladeIpc.ProtocolVersion, response.Version);
            Assert.Equal(requests[index].RequestId, response.RequestId);
            Assert.True(response.Success);
            Assert.Equal(
                [expectedCalls[index]],
                runtime.Calls.Concat(archives.Calls).Concat(shutdown.Calls));
            if (requests[index] is GetStatusRequest or SubscribeStatusRequest)
            {
                Assert.NotNull(response.Status);
                Assert.Null(response.Result);
            }
            else if (requests[index] is ExportDiagnosticsRequest)
            {
                Assert.Null(response.Status);
                Assert.Null(response.Result);
                Assert.NotNull(response.Archive);
            }
            else
            {
                Assert.Null(response.Status);
                Assert.NotNull(response.Result);
                Assert.Null(response.Archive);
            }
        }
    }

    [Theory]
    [InlineData(1, "23c09fe2-cbb8-4b74-a175-78dfb7831cf1")]
    [InlineData(3, "00000000-0000-0000-0000-000000000000")]
    public async Task HandleAsync_RejectsInvalidEnvelopeWithoutRuntimeDispatch(
        int version,
        string requestId)
    {
        var runtime = new RecordingRuntime();
        var handler = new ControlRequestHandler(runtime, new ThrowingArchiveProvider());
        var request = new GetStatusRequest(version, Guid.Parse(requestId));

        ServiceResponse response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal(OpenBladeIpc.ProtocolVersion, response.Version);
        Assert.Empty(runtime.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task HandleAsync_UnavailableOrAdvisoryControllerRuntime_UsesVerifiedProviderOnly(
        bool providerAvailable,
        bool conflict)
    {
        string path = Path.Combine(Path.GetTempPath(), $"OpenBlade.Handler.{Guid.NewGuid():N}.json");
        try
        {
            var device = new CountingDevice();
            var provider = new CountingProvider(providerAvailable, device);
            string preferencesPath = path + ".preferences";
            string markerPath = path + ".commit";
            var store = new BatteryConfigurationStore(path, preferencesPath, markerPath);
            var overrideStore = new PersistentOverrideStore(path + ".overrides");
            BatteryConfiguration configuration = await store.LoadAsync(CancellationToken.None);
            using var runtime = new ServiceControlRuntime(
                store,
                overrideStore,
                new FixedTelemetry(),
                new NullHub(),
                provider,
                new DiagnosticHistory(),
                new FixedControllerPresenceMonitor(conflict ? ["RazerAppEngine"] : []));
            runtime.Initialize(configuration, PowerSource.PluggedIn);
            var handler = new ControlRequestHandler(runtime, new ThrowingArchiveProvider());

            ServiceResponse response = await handler.HandleAsync(
                new SetFullChargeOverrideRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    true),
                CancellationToken.None);

            Assert.True(response.Success);
            Assert.Equal(ControlOperationStatus.Blocked, response.Result!.Status);
            Assert.Equal(providerAvailable ? 2 : 0, device.Calls);
            Assert.Equal(1, provider.Calls);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".preferences");
            File.Delete(path + ".commit");
        }
    }

    [Fact]
    public async Task HandleAsync_ArchiveFailureReturnsTypedFailureAfterExactlyOneAttempt()
    {
        var runtime = new RecordingRuntime();
        var archives = new FailingArchiveProvider();
        var handler = new ControlRequestHandler(runtime, archives);
        var request = new ExportDiagnosticsRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            LiveReadingsEnabled: false);

        ServiceResponse response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("diagnostic", response.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, archives.Calls);
        Assert.Empty(runtime.Calls);
        Assert.Null(response.Archive);
    }

    [Fact]
    public async Task HandleAsync_ObserveStateObservesOnceThenReturnsUpdatedStatus()
    {
        var runtime = new RecordingRuntime();
        var handler = new ControlRequestHandler(runtime, new ThrowingArchiveProvider());
        var request = new ObserveStateRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            IncludeLiveReadings: true)
        {
            RequestedLiveReadings = LiveReadingSensors.BatteryPower,
        };

        ServiceResponse response = await handler.HandleAsync(
            request,
            CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal(["Observe", "GetStatus"], runtime.Calls);
        Assert.True(runtime.LastIncludeLiveReadings);
        Assert.Equal(LiveReadingSensors.BatteryPower, runtime.LastRequestedLiveReadings);
        Assert.Equal(1, response.Status!.Revision);
        Assert.Null(response.Result);
    }

    private sealed class RecordingRuntime : IServiceControlRuntime
    {
        private long revision;

        public List<string> Calls { get; } = [];

        public ServiceStatusSnapshot GetStatus()
        {
            Calls.Add("GetStatus");
            ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
            return new ServiceStatusSnapshot(
                revision, false, true, [], null, [], "Unavailable",
                DeviceCapabilities.Unavailable(), null,
                profiles,
                new DesiredDeviceState(
                    new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                    new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                    new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                    new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
                PowerSource.PluggedIn,
                new LiveReadings(-18_000, 2_400, 2_500),
                null, null, null, false);
        }

        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) =>
            Record("UpdateProfiles");

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) =>
            Record("SetPerformanceOverride");

        public Task<ControlOperationResult> CyclePerformanceShortcutAsync(
            PerformanceShortcutSource source,
            CancellationToken token)
        {
            Assert.Equal(PerformanceShortcutSource.FunctionP, source);
            return Record("CyclePerformanceShortcut");
        }

        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) =>
            Record("SetFanOverride");

        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) =>
            Record("SetLightingOverride");

        public Task<ControlOperationResult> StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection direction,
            OverrideLifetime lifetime,
            CancellationToken token)
        {
            Assert.Equal(KeyboardBrightnessDirection.Increase, direction);
            Assert.Equal(OverrideLifetime.UntilPowerSourceChange, lifetime);
            return Record("StepKeyboardBrightness");
        }

        public Task<ControlOperationResult> SetExternalLightingOverridePolicyAsync(
            bool enabled,
            CancellationToken token)
        {
            Assert.True(enabled);
            return Record("SetExternalLightingOverridePolicy");
        }

        public Task<ControlOperationResult> SetLightingSuppressionAsync(
            LightingSuppressionReason reason,
            bool suppressed,
            CancellationToken token) => Record("SetLightingSuppression");

        public Task<ControlOperationResult> PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken token)
        {
            Assert.Equal(77, pulse.MatrixSlot);
            return Record("PulseReactiveKey");
        }

        public Task<ControlOperationResult> PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken token)
        {
            Assert.True(frame.IsWellFormed());
            return Record("PushAmbientLightingFrame");
        }

        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) =>
            Record("SetBatteryPolicy");

        public Task<ControlOperationResult> SetBatteryProtectionAsync(bool enabled, CancellationToken token) =>
            Record("SetBatteryProtection");

        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) =>
            Record("SetFullChargeOverride");

        public Task<ControlOperationResult> ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken token)
        {
            Assert.Equal(OneShotLaptopControl.FunctionKeyPrimary, value.Control);
            Assert.Equal(FunctionKeyPrimaryMode.FunctionKeys, value.FunctionKeyMode);
            return Record("ApplyOneShotLaptopControl");
        }

        public bool LastIncludeLiveReadings { get; private set; }

        public LiveReadingSensors LastRequestedLiveReadings { get; private set; }

        public Task ObserveAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken)
        {
            Calls.Add("Observe");
            LastIncludeLiveReadings = includeLiveReadings;
            revision++;
            return Task.CompletedTask;
        }

        public Task ObserveAsync(
            LiveReadingSensors requestedLiveReadings,
            CancellationToken cancellationToken)
        {
            LastRequestedLiveReadings = requestedLiveReadings;
            return ObserveAsync(
                requestedLiveReadings != LiveReadingSensors.None,
                cancellationToken);
        }
        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private Task<ControlOperationResult> Record(string name)
        {
            Calls.Add(name);
            return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Stored, "Stored"));
        }
    }

    private sealed class FixedControllerPresenceMonitor(IReadOnlyList<string> controllers)
        : IControllerPresenceMonitor
    {
        public ControllerPresenceObservation Observe() => new(controllers, null);
    }

    private sealed class RecordingShutdownSignal : IServiceShutdownSignal
    {
        public List<string> Calls { get; } = [];

        public void RequestShutdownAfterResponse() => Calls.Add("Shutdown");
    }

    private sealed class FixedTelemetry : IPowerStateProvider
    {
        public PowerSource CapturePowerSource() => PowerSource.PluggedIn;

        public int? CaptureBatteryPowerFlowMilliwatts() => null;
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
        public Task<FanRpmReadings?> ReadFanRpmAsync(CancellationToken cancellationToken) =>
            Count<FanRpmReadings?>(null);
        public Task<AdapterPowerState> ReadAdapterPowerStateAsync(CancellationToken cancellationToken) =>
            Count(AdapterPowerState.FullPower280W);
        public Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken) => Count();
        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken) => Count();
        public Task SetChargeLimitAsync(BatteryPolicy value, CancellationToken cancellationToken) => Count();
        public Task SetBatteryProtectionOffAsync(CancellationToken cancellationToken) => Count();
        public Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken) => Count();
        public Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken) => Count();
        private Task<T> Count<T>(T value) { Calls++; return Task.FromResult(value); }
        private Task Count() { Calls++; return Task.CompletedTask; }
    }

    private sealed class ThrowingArchiveProvider : IDiagnosticArchiveProvider
    {
        public Task<DiagnosticArchive> CreateAsync(
            bool liveReadingsEnabled,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Archive export was not expected.");
    }

    private sealed class RecordingArchiveProvider : IDiagnosticArchiveProvider
    {
        public List<string> Calls { get; } = [];

        public Task<DiagnosticArchive> CreateAsync(
            bool liveReadingsEnabled,
            CancellationToken cancellationToken)
        {
            Calls.Add("ExportDiagnostics");
            return Task.FromResult(new DiagnosticArchive(
                "OpenBlade-diagnostics.zip",
                [0x50, 0x4B, 0x03, 0x04]));
        }
    }

    private sealed class FailingArchiveProvider : IDiagnosticArchiveProvider
    {
        public int Calls { get; private set; }

        public Task<DiagnosticArchive> CreateAsync(
            bool liveReadingsEnabled,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidDataException("Diagnostic archive exceeds the 32 KiB limit.");
        }
    }
}
