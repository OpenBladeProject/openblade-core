using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ControlCoordinatorTests
{
    [Theory]
    [InlineData(DeviceSubsystem.Performance, "SetPerformance")]
    [InlineData(DeviceSubsystem.Fans, "SetFans")]
    [InlineData(DeviceSubsystem.Battery, "SetBattery")]
    [InlineData(DeviceSubsystem.Lighting, "SetLighting")]
    public async Task ScopedApply_UsesOneNamedSetterAndOneMatchingReadback(
        DeviceSubsystem subsystem,
        string setterCall)
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await ApplyScopedAsync(coordinator, subsystem);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
        Assert.Equal(["Capabilities", "State", setterCall, "State"], device.Calls);
        Assert.Equal(1, device.TotalSetterCalls);
    }

    [Theory]
    [InlineData(DeviceSubsystem.Performance)]
    [InlineData(DeviceSubsystem.Fans)]
    [InlineData(DeviceSubsystem.Battery)]
    [InlineData(DeviceSubsystem.Lighting)]
    public async Task ScopedApply_WhenAlreadyMatching_PerformsNoSetterOrSecondRead(
        DeviceSubsystem subsystem)
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await ApplyScopedAsync(coordinator, subsystem);

        Assert.Equal(ControlResultStatus.Unchanged, result.Status);
        Assert.Equal(["Capabilities", "State"], device.Calls);
        Assert.Equal(0, device.TotalSetterCalls);
    }

    [Fact]
    public async Task ScopedApply_UnsupportedNamedValue_BlocksOnlyBeforeSetterAndReadback()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.Capabilities = device.Capabilities with
        {
            FanModes = new HashSet<FanMode> { FanMode.Automatic },
        };
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await coordinator.ApplyFanAsync(
            FanMode.Maximum,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Equal(["Capabilities"], device.Calls);
        Assert.Equal(0, device.TotalSetterCalls);
    }

    [Fact]
    public async Task ScopedApply_InvalidLighting_IsBlockedBeforeDeviceAccess()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await coordinator.ApplyLightingAsync(
            new LightingSettings(LightingEffect.Spectrum, new RgbColor(1, 2, 3), 20),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Empty(device.Calls);
    }

    [Fact]
    public async Task ScopedApply_MismatchedReadbackFailsWithoutRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.AutoConfirmWrites = false;
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, result.Status);
        Assert.Contains("indeterminate", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Capabilities", "State", "SetPerformance", "State"], device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedApply_PostWriteReadbackFailureFailsWithoutRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, result.Status);
        Assert.Contains("indeterminate", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Capabilities", "State", "SetPerformance", "State"], device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedApply_ControllerWarningIsAdvisory()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(
            device,
            new FakeHardwareAccessGuard(["RazerAppEngine"], "enumeration warning"));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedApply_CancellationBeforeSetterPropagatesWithoutWrite()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        using var cancellation = new CancellationTokenSource();
        device.AfterReadState = call =>
        {
            if (call == 1)
            {
                cancellation.Cancel();
            }
        };
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ApplyPerformanceAsync(
                PerformancePreset.Balanced,
                cancellation.Token));

        Assert.Equal(["Capabilities", "State"], device.Calls);
        Assert.Equal(0, device.TotalSetterCalls);
    }

    [Fact]
    public async Task ScopedApply_CancellationAfterSetterPropagatesWithoutReadbackOrRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        using var cancellation = new CancellationTokenSource();
        device.AfterSetter = _ => cancellation.Cancel();
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ApplyPerformanceAsync(
                PerformancePreset.Balanced,
                cancellation.Token));

        Assert.Equal(["Capabilities", "State", "SetPerformance"], device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task TemporaryFullCharge_MatchesNullBatteryReadback()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));
        BatteryPolicy temporary = BatteryPolicy.Create(80, temporaryFullCharge: true);

        ControlResult result = await coordinator.ApplyBatteryAsync(
            temporary,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
        Assert.Null(device.State.Battery);
        Assert.Equal(["Capabilities", "State", "SetBattery", "State"], device.Calls);
    }

    [Fact]
    public async Task ApplyDesiredState_UsesOneInitialAndOneFinalAggregateRead()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.All(
            [results.Performance, results.Fans, results.Battery, results.Lighting],
            result => Assert.Equal(ControlResultStatus.Applied, result.Status));
        Assert.Equal(
            [
                "Capabilities",
                "State",
                "SetPerformance",
                "SetFans",
                "SetBattery",
                "SetLighting",
                "State",
            ],
            device.Calls);
        Assert.Equal(4, device.TotalSetterCalls);
    }

    [Fact]
    public async Task ApplyDesiredState_FinalMismatchFailsOnlyMismatchedAttempt()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.SuppressConfirmationFor = DeviceSubsystem.Fans;
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Failed, results.Fans.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Battery.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Lighting.Status);
        Assert.Equal(1, device.FanSetCalls);
        Assert.Equal(2, device.StateReadCalls);
    }

    [Fact]
    public async Task ApplyDesiredState_TerminalSetterFailureStopsLaterWritesButStillReadsBackOnce()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.PerformanceFailure = new IOException("device disconnected");
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Fans.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Battery.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Lighting.Status);
        Assert.Equal(["Capabilities", "State", "SetPerformance", "State"], device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
        Assert.Equal(0, device.FanSetCalls);
        Assert.Equal(0, device.BatterySetCalls);
        Assert.Equal(0, device.LightingSetCalls);
    }

    private static Task<ControlResult> ApplyScopedAsync(
        ControlCoordinator coordinator,
        DeviceSubsystem subsystem) => subsystem switch
        {
            DeviceSubsystem.Performance => coordinator.ApplyPerformanceAsync(
                PerformancePreset.Balanced,
                CancellationToken.None),
            DeviceSubsystem.Fans => coordinator.ApplyFanAsync(
                FanMode.Maximum,
                CancellationToken.None),
            DeviceSubsystem.Battery => coordinator.ApplyBatteryAsync(
                BatteryPolicy.Create(75),
                CancellationToken.None),
            DeviceSubsystem.Lighting => coordinator.ApplyLightingAsync(
                TargetLighting,
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(subsystem)),
        };

    private static RecordingBladeDevice CreateMismatchedDevice() => new()
    {
        State = new ConfirmedDeviceState(
            PerformancePreset.Silent,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            new LightingSettings(LightingEffect.Off, default, 0)),
    };

    private static RecordingBladeDevice CreateMatchingDevice() => new()
    {
        State = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            BatteryPolicy.Create(75),
            TargetLighting),
    };

    private static DesiredDeviceState CreateDesiredState() => new(
        new ResolvedSetting<PerformancePreset>(
            PerformancePreset.Balanced,
            SettingValueSource.PowerProfile),
        new ResolvedSetting<FanMode>(FanMode.Maximum, SettingValueSource.PowerProfile),
        new ResolvedSetting<LightingSettings>(
            TargetLighting,
            SettingValueSource.PowerProfile),
        BatteryPolicy.Create(75));

    private static LightingSettings TargetLighting { get; } = new(
        LightingEffect.Static,
        new RgbColor(10, 20, 30),
        40);

    private sealed class FakeHardwareAccessGuard(
        IReadOnlyList<string> conflicts,
        string? failure = null) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new(conflicts, failure);
    }

    private sealed class RecordingBladeDevice : IBladeDevice
    {
        public List<string> Calls { get; } = [];

        public DeviceCapabilities Capabilities { get; set; } =
            DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>
                {
                    LightingEffect.Off,
                    LightingEffect.Static,
                },
            };

        public ConfirmedDeviceState State { get; set; } = new(null, null, null, null);

        public bool AutoConfirmWrites { get; set; } = true;

        public DeviceSubsystem? SuppressConfirmationFor { get; set; }

        public int? ReadStateFailureOnCall { get; set; }

        public Exception? PerformanceFailure { get; set; }

        public Action<int>? AfterReadState { get; set; }

        public Action<DeviceSubsystem>? AfterSetter { get; set; }

        public int PerformanceSetCalls { get; private set; }

        public int FanSetCalls { get; private set; }

        public int BatterySetCalls { get; private set; }

        public int LightingSetCalls { get; private set; }

        public int StateReadCalls { get; private set; }

        public int TotalSetterCalls => PerformanceSetCalls
            + FanSetCalls
            + BatterySetCalls
            + LightingSetCalls;

        public Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("Capabilities");
            return Task.FromResult(Capabilities);
        }

        public Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("State");
            StateReadCalls++;
            if (ReadStateFailureOnCall == StateReadCalls)
            {
                throw new IOException("readback failed");
            }

            AfterReadState?.Invoke(StateReadCalls);
            return Task.FromResult(State);
        }

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetPerformance");
            PerformanceSetCalls++;
            if (PerformanceFailure is not null)
            {
                throw PerformanceFailure;
            }

            if (ShouldConfirm(DeviceSubsystem.Performance))
            {
                State = State with { Performance = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Performance);
            return Task.CompletedTask;
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetFans");
            FanSetCalls++;
            if (ShouldConfirm(DeviceSubsystem.Fans))
            {
                State = State with { Fans = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Fans);
            return Task.CompletedTask;
        }

        public Task SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetBattery");
            BatterySetCalls++;
            if (ShouldConfirm(DeviceSubsystem.Battery))
            {
                State = State with
                {
                    Battery = value.TemporaryFullCharge ? null : value,
                };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Battery);
            return Task.CompletedTask;
        }

        public Task SetLightingAsync(
            LightingSettings value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetLighting");
            LightingSetCalls++;
            if (ShouldConfirm(DeviceSubsystem.Lighting))
            {
                State = State with { Lighting = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Lighting);
            return Task.CompletedTask;
        }

        private bool ShouldConfirm(DeviceSubsystem subsystem) =>
            AutoConfirmWrites && SuppressConfirmationFor != subsystem;
    }
}
