using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    // A power change abandons pending confirmations, including an unresolved
    // uncertain GPU submission. The destination thermal step is applied instead of
    // being refused on the write block those confirmations held.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PowerChange_WithUnresolvedPending_AppliesTheDestinationThermalStep(bool gpu)
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = StepLessGpuCapabilities().GpuOverclock,
        };
        context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.Unknown;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        if (gpu)
        {
            context.Device.AfterGpuClockSet = () =>
            {
                context.Device.GpuClockOffsetManagementState = GpuClockOffsetManagementState.SubmissionUncertain;
                context.Device.ConfirmedState = context.Device.ConfirmedState with { GpuClockOffsets = null };
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "GPU clock tuning failed. Unable to set the GPU memory clock offset.");
            };
            await context.Runtime.SetGpuClockTuningAsync(
                new GpuClockTuningSettings(true, new GpuClockOffsetSettings(200, 500)),
                OverrideLifetime.UntilPowerSourceChange, static () => true, CancellationToken.None);
            context.Device.AfterGpuClockSet = null;
        }
        else
        {
            context.Device.PerformanceFailures.Enqueue(
                new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK."));
            context.Device.MutatePerformanceBeforeQueuedFailure = true;
            context.Device.BeforeQueuedPerformanceFailure = () =>
                context.Device.ReadStateFailure = new IOException("Injected lost readback.");
            await context.Runtime.SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(PerformancePreset.Silent, OverrideLifetime.UntilPowerSourceChange),
                CancellationToken.None);
            context.Device.ReadStateFailure = null;
            // The preset stays unreadable, so the pending confirmation stays unavailable.
            context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = null };
        }
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        Assert.Contains(DeviceSubsystem.Performance, before.UnconfirmedWrites);
        Assert.True(before.WritesBlocked);
        if (!gpu)
        {
            context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Silent };
        }

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.DoesNotContain("Thermal write authority closed", status.Message, StringComparison.Ordinal);
        Assert.Equal(status.DesiredState.Performance.Value, status.ConfirmedState?.Performance);
        Assert.Equal(status.DesiredState.Performance.Value, context.Device.PerformanceWrites[^1]);
        Assert.Empty(status.UnconfirmedWrites);
        Assert.False(status.WritesBlocked);
    }
}
