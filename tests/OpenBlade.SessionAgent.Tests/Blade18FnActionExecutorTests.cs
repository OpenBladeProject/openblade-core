using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class Blade18FnActionExecutorTests
{
    [Fact]
    public async Task PerformanceShortcutRejectsDuplicateExecutionRoute()
    {
        var client = new RecordingServiceClient(Status());
        var applier = new RecordingDisplayProfileApplier(DisplayState("fixed-60"));
        var touchpad = new RecordingTouchpadController();
        var executor = new Blade18FnActionExecutor(client, applier, touchpad);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(
                Blade18FnShortcut.Performance,
                CancellationToken.None));

        Assert.Contains("performance shortcut pump", failure.Message);
        Assert.Equal(0, client.GetStatusCalls);
        Assert.Equal(0, applier.QueryCalls);
        Assert.Equal(0, touchpad.ToggleCalls);
    }

    [Theory]
    [InlineData(AdmissionFailure.Unverified)]
    [InlineData(AdmissionFailure.OtherModel)]
    [InlineData(AdmissionFailure.InactiveSession)]
    [InlineData(AdmissionFailure.WritesBlocked)]
    [InlineData(AdmissionFailure.UnknownPower)]
    public async Task AdmissionFailureDoesNotRunCurrentUserAction(
        AdmissionFailure failure)
    {
        ServiceStatusSnapshot status = failure switch
        {
            AdmissionFailure.Unverified => Status() with { DeviceVerified = false },
            AdmissionFailure.OtherModel => Status() with
            {
                Firmware = Firmware("RZ09-0581"),
            },
            AdmissionFailure.InactiveSession => Status() with
            {
                ControlAccess = SessionControlAccess.InactiveSession,
            },
            AdmissionFailure.WritesBlocked => Status() with { WritesBlocked = true },
            AdmissionFailure.UnknownPower => Status() with
            {
                ActivePowerSource = PowerSource.ExternalUnknown,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var client = new RecordingServiceClient(status);
        var applier = new RecordingDisplayProfileApplier(DisplayState("fixed-60"));
        var touchpad = new RecordingTouchpadController();
        var executor = new Blade18FnActionExecutor(client, applier, touchpad);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            Blade18FnShortcut.RefreshRate,
            CancellationToken.None));

        Assert.Equal(1, client.GetStatusCalls);
        Assert.Equal(0, applier.QueryCalls);
        Assert.Empty(applier.RefreshWrites);
        Assert.Equal(0, touchpad.ToggleCalls);
    }

    [Theory]
    [InlineData("fixed-60", "fixed-120")]
    [InlineData("fixed-120", "fixed-60")]
    [InlineData("dynamic-60-120", "fixed-60")]
    public async Task RefreshRateCyclesOnlyFixedOptions(
        string current,
        string expected)
    {
        var client = new RecordingServiceClient(Status(PowerSource.Battery));
        var applier = new RecordingDisplayProfileApplier(DisplayState(current));
        var executor = new Blade18FnActionExecutor(
            client,
            applier,
            new RecordingTouchpadController());

        await executor.ExecuteAsync(
            Blade18FnShortcut.RefreshRate,
            CancellationToken.None);

        Assert.Equal(1, applier.QueryCalls);
        Assert.Equal([(PowerSource.Battery, expected)], applier.RefreshWrites);
    }

    [Fact]
    public async Task UnavailableRefreshRateFailsWithoutMutation()
    {
        DisplaySettingsSnapshot unavailable = DisplayState("fixed-60") with
        {
            IsAvailable = false,
            Message = "The internal display is unavailable.",
        };
        var applier = new RecordingDisplayProfileApplier(unavailable);
        var executor = CreateExecutor(applier);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(
                Blade18FnShortcut.RefreshRate,
                CancellationToken.None));

        Assert.Equal("The internal display is unavailable.", failure.Message);
        Assert.Empty(applier.RefreshWrites);
    }

    [Fact]
    public async Task SingleFixedRefreshRateIsNoOp()
    {
        DisplaySettingsSnapshot single = DisplayState("fixed-60") with
        {
            RefreshOptions =
            [
                Fixed("fixed-60", 60),
                Dynamic("dynamic-60-120", 60, 120),
            ],
        };
        var applier = new RecordingDisplayProfileApplier(single);
        var executor = CreateExecutor(applier);

        await executor.ExecuteAsync(
            Blade18FnShortcut.RefreshRate,
            CancellationToken.None);

        Assert.Equal(1, applier.QueryCalls);
        Assert.Empty(applier.RefreshWrites);
    }

    [Fact]
    public async Task StalePowerSourceBlockIsReturnedWithoutReplay()
    {
        var applier = new RecordingDisplayProfileApplier(DisplayState("fixed-60"))
        {
            RefreshResult = Result(
                SessionDisplayOperationStatus.Blocked,
                DisplayState("fixed-60"),
                "The active power source changed before the display operation began."),
        };
        var executor = CreateExecutor(applier);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(
                Blade18FnShortcut.RefreshRate,
                CancellationToken.None));

        Assert.Contains("power source changed", failure.Message);
        Assert.Single(applier.RefreshWrites);
    }

    [Fact]
    public async Task TouchpadToggleRunsOnceAfterFreshAdmission()
    {
        var client = new RecordingServiceClient(Status(PowerSource.UsbC));
        var touchpad = new RecordingTouchpadController();
        var executor = new Blade18FnActionExecutor(
            client,
            new RecordingDisplayProfileApplier(DisplayState("fixed-60")),
            touchpad);

        await executor.ExecuteAsync(
            Blade18FnShortcut.Touchpad,
            CancellationToken.None);

        Assert.Equal(1, client.GetStatusCalls);
        Assert.Equal(1, touchpad.ToggleCalls);
    }

    private static Blade18FnActionExecutor CreateExecutor(
        RecordingDisplayProfileApplier applier) => new(
            new RecordingServiceClient(Status()),
            applier,
            new RecordingTouchpadController());

    private static ServiceStatusSnapshot Status(
        PowerSource powerSource = PowerSource.PluggedIn)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "Ready",
            Capabilities: DeviceCapabilities.Unavailable(),
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    profiles.PluggedIn.Performance,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    profiles.PluggedIn.Fans,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    profiles.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            ActivePowerSource: powerSource,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false,
            Firmware: Firmware("RZ09-0484"),
            ControlAccess: SessionControlAccess.Active);
    }

    private static FirmwareVersions Firmware(string modelNumber) => new(
        "2.09",
        "1.00",
        "Unavailable",
        "Unavailable",
        modelNumber);

    private static DisplaySettingsSnapshot DisplayState(string current) => new(
        IsAvailable: true,
        DisplayName: "Internal display",
        Width: 2560,
        Height: 1600,
        CurrentRefreshKey: current,
        RefreshOptions:
        [
            Fixed("fixed-60", 60),
            Dynamic("dynamic-60-120", 60, 120),
            Fixed("fixed-120", 120),
        ],
        HdrSupported: true,
        HdrEnabled: false,
        ColorProfilesEnabled: false,
        ActiveColorProfileId: null,
        ColorProfiles: [],
        Message: null);

    private static DisplayRefreshOption Fixed(string key, int hertz) =>
        new(key, $"{hertz} Hz", hertz, hertz, hertz, false);

    private static DisplayRefreshOption Dynamic(
        string key,
        int minimumHertz,
        int maximumHertz) => new(
            key,
            $"Dynamic {minimumHertz}-{maximumHertz} Hz",
            minimumHertz,
            maximumHertz,
            maximumHertz,
            true);

    private static SessionDisplayExecutionResult Result(
        SessionDisplayOperationStatus status,
        DisplaySettingsSnapshot state,
        string? error = null) => new(status, error, state);

    public enum AdmissionFailure
    {
        Unverified,
        OtherModel,
        InactiveSession,
        WritesBlocked,
        UnknownPower,
    }

    private sealed class RecordingServiceClient(ServiceStatusSnapshot status)
        : OpenBlade.Testing.UnavailableServiceClient
    {
        public int GetStatusCalls { get; private set; }

        public override Task<ServiceStatusSnapshot> GetStatusAsync(
            CancellationToken token)
        {
            GetStatusCalls++;
            return Task.FromResult(status);
        }
    }

    private sealed class RecordingDisplayProfileApplier(DisplaySettingsSnapshot state)
        : IDisplayProfileApplier
    {
        public int QueryCalls { get; private set; }

        public List<(PowerSource PowerSource, string Key)> RefreshWrites { get; } = [];

        public SessionDisplayExecutionResult QueryResult { get; set; } =
            Result(SessionDisplayOperationStatus.Succeeded, state);

        public SessionDisplayExecutionResult RefreshResult { get; set; } =
            Result(SessionDisplayOperationStatus.Succeeded, state);

        public Task<SessionDisplayExecutionResult> QueryAsync(
            CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return Task.FromResult(QueryResult);
        }

        public Task<SessionDisplayExecutionResult> SetRefreshRateAsync(
            PowerSource expectedPowerSource,
            string refreshRateKey,
            CancellationToken cancellationToken = default)
        {
            RefreshWrites.Add((expectedPowerSource, refreshRateKey));
            return Task.FromResult(RefreshResult);
        }

        public Task<SessionDisplayExecutionResult> SetHdrAsync(
            PowerSource expectedPowerSource,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> SetColorProfileAsync(
            PowerSource expectedPowerSource,
            string colorProfileKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> ApplyProfileAsync(
            PowerSource expectedPowerSource,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> ApplyProfileForTransitionAsync(
            PowerSource expectedPowerSource,
            long expectedGeneration,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTouchpadController : IBladeTouchpadController
    {
        public int ToggleCalls { get; private set; }

        public Task ToggleAsync(CancellationToken cancellationToken)
        {
            ToggleCalls++;
            return Task.CompletedTask;
        }
    }
}
