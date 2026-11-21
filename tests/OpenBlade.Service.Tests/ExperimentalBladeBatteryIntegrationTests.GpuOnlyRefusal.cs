using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// Real RZ09-0529 and RZ09-0530 providers with the real NVML controller: a GPU-only
// Performance step that the driver refuses before submission does not stop the
// fan step, which keeps its own admission. A refusal is the driver rejecting every
// write (KnownNotApplied from the readback) or an unreadable driver
// (CapabilityUnavailable).
public sealed partial class ExperimentalBladeBatteryIntegrationTests
{
    private static async Task<(FullTransitionApplyOutcome Outcome, int FirmwareSetters, IGpuClockOffsetManagementDevice Managed)>
        ApplyGpuOnlyStepThenFixedFansAsync(int productId, string driver, bool continuation)
    {
        GpuOffsetHarness harness = CreateGpuOffsetHarness(productId, "NVIDIA GeForce RTX 5070 Laptop GPU");
        using WindowsVerifiedBladeDeviceProvider provider = harness.Provider;
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        harness.Activity.Active = false;
        ConfirmedDeviceState before = await device.ReadStateAsync(CancellationToken.None);
        Assert.NotNull(before.Performance);
        if (driver == "refuses")
        {
            harness.Api.SetStatus = (_, _) => 999;
        }
        else if (driver == "unreadable")
        {
            harness.Api.QueryFails = true;
        }
        var desired = new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(before.Performance.Value, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Fixed, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(before.Lighting ?? LightingSettingsFactory.Off(), SettingValueSource.PowerProfile),
            new DesiredBatteryState(
                BatteryPolicy.Create(before.Battery?.ChargeLimitPercent ?? 80),
                before.Battery?.ProtectionEnabled ?? true,
                false))
        {
            GpuClockTuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)),
            FanControl = new FanControlSettings(FanMode.Fixed, 3700, ManualFanCurve.Default),
        };
        int commands = harness.Io.Commands.Count;

        FullTransitionApplyOutcome outcome = await new ControlCoordinator(device).ApplyDesiredStateOutcomeAsync(
            desired,
            CancellationToken.None,
            continuationScopeResolver: continuation
                ? (_, _) => new ContinuationWriteScope(
                    new HashSet<DeviceSubsystem> { DeviceSubsystem.Fans, DeviceSubsystem.Performance },
                    new HashSet<DeviceSubsystem> { DeviceSubsystem.Performance })
                : null);
        // Thermal (0D/02) and fan-target (0D/01) setters the step sent.
        int firmwareSetters = harness.Io.Commands.Skip(commands).Count(command =>
            command == ((byte)0x0D, (byte)0x02) || command == ((byte)0x0D, (byte)0x01));
        return (outcome, firmwareSetters, (IGpuClockOffsetManagementDevice)device);
    }

    [Theory]
    [InlineData(0x02C7, "refuses", true)]
    [InlineData(0x02C7, "unreadable", true)]
    [InlineData(0x02C5, "refuses", true)]
    [InlineData(0x02C5, "unreadable", false)]
    [InlineData(0x02C7, "refuses", false)]
    public async Task GpuOffsets_RefusedGpuOnlyStepLeavesTheFanStepToItsOwnAdmission(
        int productId,
        string driver,
        bool continuation)
    {
        (FullTransitionApplyOutcome accepted, _, _) =
            await ApplyGpuOnlyStepThenFixedFansAsync(productId, "accepts", continuation);
        (FullTransitionApplyOutcome refused, int firmwareSetters, IGpuClockOffsetManagementDevice managed) =
            await ApplyGpuOnlyStepThenFixedFansAsync(productId, driver, continuation);

        Assert.Equal(ControlResultStatus.Applied, accepted.Results.Performance.Status);
        Assert.NotEqual(ControlResultStatus.Applied, refused.Results.Performance.Status);
        Assert.Contains("GPU clock tuning", refused.Results.Performance.Message);
        Assert.Contains(DeviceSubsystem.Performance, refused.GpuClockOffsetsOnlyTried ?? new HashSet<DeviceSubsystem>());
        Assert.DoesNotContain(DeviceSubsystem.Performance, refused.WriteAttempted);
        Assert.Equal(0, firmwareSetters);
        Assert.NotEqual(GpuClockOffsetManagementState.SubmissionUncertain, managed.GpuClockOffsetManagementState);
        // The fan step reached its own admission exactly as it does when the driver
        // accepts the offsets.
        Assert.DoesNotContain("No further hardware writes were attempted", refused.Results.Fans.Message);
        Assert.Equal(accepted.Results.Fans, refused.Results.Fans);
    }
}
