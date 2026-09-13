using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class WriteConfirmationPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Battery_UnknownTemporaryFullChargeKeepsWritePending(bool protectionEnabled)
    {
        var expectation = new WriteConfirmationExpectation(
            Desired() with
            {
                Battery = new DesiredBatteryState(BatteryPolicy.Create(80), protectionEnabled, false),
            },
            Confirmed());

        Assert.Equal(
            PendingConfirmationResult.Unavailable,
            Match(DeviceSubsystem.Battery, expectation, Confirmed() with { Battery = null }));
        Assert.Equal(
            PendingConfirmationResult.Unavailable,
            Match(DeviceSubsystem.Battery, expectation, Confirmed() with
            {
                Battery = new ConfirmedBatteryState(protectionEnabled, protectionEnabled ? 80 : null, null),
            }));
    }

    [Fact]
    public void Battery_RequiresExactTemporaryFlagButIgnoresLimitWhenProtectionIsOff()
    {
        var expectation = new WriteConfirmationExpectation(Desired(), Confirmed());
        Assert.Equal(
            PendingConfirmationResult.Mismatch,
            Match(DeviceSubsystem.Battery, expectation, Confirmed() with
            {
                Battery = new ConfirmedBatteryState(true, 80, true),
            }));

        expectation = expectation with
        {
            Target = Desired() with
            {
                Battery = new DesiredBatteryState(BatteryPolicy.Create(80), false, false),
            },
        };
        Assert.Equal(
            PendingConfirmationResult.Match,
            Match(DeviceSubsystem.Battery, expectation, Confirmed() with
            {
                Battery = new ConfirmedBatteryState(false, null, false),
            }));
    }

    [Fact]
    public void ManualPerformance_MatchesGenericManualStateBeforePresetAndGpuChecks()
    {
        var expectation = new WriteConfirmationExpectation(
            Desired() with
            {
                FanControl = FanControlSettings.FromLegacy(FanMode.Maximum),
                GpuClockTuning = GpuClockTuningSettings.Disabled,
            },
            Confirmed());
        ConfirmedDeviceState observed = Confirmed() with
        {
            Performance = null,
            Fans = FanMode.Fixed,
        };

        Assert.False(WriteConfirmationPolicy.RequiresGpuClockOffsetManagementState(
            DeviceSubsystem.Performance, expectation, observed));
        Assert.Equal(PendingConfirmationResult.Match, Match(DeviceSubsystem.Performance, expectation, observed));
        Assert.Equal(PendingConfirmationResult.Mismatch, Match(DeviceSubsystem.Fans, expectation, observed));
    }

    [Fact]
    public void DisabledGpuReset_RequiresExplicitConfirmedManagementEvidence()
    {
        var expectation = new WriteConfirmationExpectation(
            Desired() with { GpuClockTuning = GpuClockTuningSettings.Disabled },
            Confirmed());
        Assert.True(WriteConfirmationPolicy.RequiresGpuClockOffsetManagementState(
            DeviceSubsystem.Performance, expectation, Confirmed()));
        Assert.Equal(
            PendingConfirmationResult.Unavailable,
            Match(DeviceSubsystem.Performance, expectation, Confirmed()));

        foreach (GpuClockOffsetManagementState state in Enum.GetValues<GpuClockOffsetManagementState>())
        {
            Assert.Equal(
                state is GpuClockOffsetManagementState.ResetConfirmed
                    or GpuClockOffsetManagementState.RelinquishedDefaultConfirmed
                    ? PendingConfirmationResult.Match
                    : PendingConfirmationResult.Mismatch,
                WriteConfirmationPolicy.Match(DeviceSubsystem.Performance, expectation, Confirmed(), state));
        }

        ConfirmedDeviceState offsetsStillPresent = Confirmed() with { GpuClockOffsets = Offsets(0, 0) };
        Assert.False(WriteConfirmationPolicy.RequiresGpuClockOffsetManagementState(
            DeviceSubsystem.Performance, expectation, offsetsStillPresent));
        Assert.Equal(
            PendingConfirmationResult.Mismatch,
            WriteConfirmationPolicy.Match(
                DeviceSubsystem.Performance,
                expectation,
                offsetsStillPresent,
                GpuClockOffsetManagementState.ResetConfirmed));
    }

    [Fact]
    public void PerformanceWithoutGpuIntent_PreservesPreviouslyConfirmedOffsets()
    {
        ConfirmedDeviceState before = Confirmed() with { GpuClockOffsets = Offsets(100, 200) };
        var expectation = new WriteConfirmationExpectation(Desired(), before);
        Assert.Equal(PendingConfirmationResult.Unavailable, Match(DeviceSubsystem.Performance, expectation, Confirmed()));
        Assert.Equal(PendingConfirmationResult.Match, Match(DeviceSubsystem.Performance, expectation, before));
        Assert.Equal(
            PendingConfirmationResult.Mismatch,
            Match(DeviceSubsystem.Performance, expectation, before with { GpuClockOffsets = Offsets(0, 0) }));
        Assert.Equal(
            PendingConfirmationResult.Mismatch,
            Match(DeviceSubsystem.Performance, expectation with { PreWriteState = Confirmed() }, before));
    }

    [Fact]
    public void HyperBoostExit_RequiresRestoredPerformanceAndAutomaticFansInBothSubsystems()
    {
        var expectation = new WriteConfirmationExpectation(
            Desired() with { GpuClockTuning = GpuClockTuningSettings.Disabled },
            Confirmed(),
            HyperBoostActive: false,
            HyperBoostBaseMode: PerformancePreset.Balanced);
        (ConfirmedDeviceState Observed, PendingConfirmationResult Result)[] cases =
        [
            (Confirmed(), PendingConfirmationResult.Match),
            (Confirmed() with { Performance = PerformancePreset.Performance }, PendingConfirmationResult.Mismatch),
            (Confirmed() with { Fans = FanMode.Maximum }, PendingConfirmationResult.Mismatch),
            (Confirmed() with { Performance = null }, PendingConfirmationResult.Unavailable),
            (Confirmed() with { Fans = null }, PendingConfirmationResult.Unavailable),
            (Confirmed() with { HyperBoostActive = true, Fans = null }, PendingConfirmationResult.Mismatch),
        ];
        foreach (DeviceSubsystem subsystem in new[] { DeviceSubsystem.Performance, DeviceSubsystem.Fans })
        {
            foreach ((ConfirmedDeviceState observed, PendingConfirmationResult result) in cases)
            {
                Assert.False(WriteConfirmationPolicy.RequiresGpuClockOffsetManagementState(subsystem, expectation, observed));
                Assert.Equal(result, Match(subsystem, expectation, observed));
            }
        }
    }

    private static PendingConfirmationResult Match(
        DeviceSubsystem subsystem,
        WriteConfirmationExpectation expectation,
        ConfirmedDeviceState observed) => WriteConfirmationPolicy.Match(
            subsystem,
            expectation,
            observed,
            gpuClockOffsetManagementState: null);

    private static DesiredDeviceState Desired() => new(
        new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
        new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
        new ResolvedSetting<LightingSettings>(LightingSettingsFactory.Off(), SettingValueSource.PowerProfile),
        new DesiredBatteryState(BatteryPolicy.Create(80), true, false));

    private static ConfirmedDeviceState Confirmed() => new(
        PerformancePreset.Balanced,
        FanMode.Automatic,
        new ConfirmedBatteryState(true, 80, false),
        LightingSettingsFactory.Off());

    private static GpuClockOffsetCapabilities Offsets(int core, int memory) => new(
        "Test NVIDIA GPU",
        new GpuClockOffsetRange(-1000, 400, core),
        new GpuClockOffsetRange(-1000, 3000, memory));
}
