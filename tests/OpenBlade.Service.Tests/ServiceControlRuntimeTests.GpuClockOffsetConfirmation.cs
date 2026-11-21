using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

// A pending thermal confirmation with a GPU half is judged from the device's
// GPU management state and managed offset observation, in ordinary observation
// and in experimental recovery. An unobserved GPU half is retired, never matched.
public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData((ushort)0x02C7)]
    [InlineData((ushort)0x02C5)]
    public async Task GpuTuning_LimitedUsbCAdapterIsConfirmedExternalPower(ushort product)
    {
        // The service gate is PowerSourcePolicy.IsExternal, which includes the
        // limited-adapter UsbC profile. Synapse hides its panel there; OpenBlade
        // documents that divergence instead of adding a gate.
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.UsbC);
        DeviceCapabilities experimental = ExperimentalBladePort.CreateCapabilities(
            ExperimentalBladeModels.All.Single(model => model.ProductId == product), enabled: true);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = experimental.GpuOverclock,
        };
        PowerProfile usbC = context.Runtime.GetStatus().Profiles.UsbC;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            usbC.Performance,
            usbC.Fans,
            ConfirmedBattery(80),
            usbC.Lighting)
        {
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(125, 500));

        ControlOperationResult result = await context.Runtime.SetGpuClockTuningAsync(
            tuning, OverrideLifetime.Persistent, static () => true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Device.GpuClockSetCalls);
        Assert.Equal(tuning.EffectiveOffsets, context.Device.ConfirmedState.GpuClockOffsets?.Current);
    }

    [Fact]
    public async Task GpuTuning_BatteryIsRefusedWithoutNvidiaWrite()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.Battery);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = ExperimentalBladePort.CreateGpuOverclockCapabilities(),
        };
        PowerProfile battery = context.Runtime.GetStatus().Profiles.Battery;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            battery.Performance,
            battery.Fans,
            ConfirmedBattery(80),
            battery.Lighting)
        {
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime.SetGpuClockTuningAsync(
            new GpuClockTuningSettings(true, new GpuClockOffsetSettings(125, 500)),
            OverrideLifetime.Persistent, static () => true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Device.GpuClockSetCalls);
    }

    // GPU-only write: the NVML write lands and the device holds it as managed,
    // but the follow-up read is lost and experimental writes pause.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExperimentalResume_JudgesPendingGpuWriteFromManagedObservation(bool observationAvailable)
    {
        TestContext context = CreateWritableContext();
        context.Provider.AvailableWhenExperimentalAllowed = true;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = ExperimentalBladePort.CreateGpuOverclockCapabilities(),
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(125, 500));
        context.Device.AfterGpuClockSet = () =>
        {
            context.Device.ReadStateFailure = new IOException("Injected lost GPU readback.");
            context.Provider.IsExperimentalWritesPaused = true;
        };

        ControlOperationResult write = await context.Runtime.SetGpuClockTuningAsync(
            tuning, OverrideLifetime.Persistent, static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Failed, write.Status);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(1, context.Device.GpuClockSetCalls);

        context.Device.ReadStateFailure = null;
        context.Device.AfterGpuClockSet = null;
        if (!observationAvailable)
        {
            // The device still manages the offsets, but its confirmation was lost
            // and the dGPU cannot be read: recovery carries no GPU offsets.
            context.Device.ConfirmedState = context.Device.ConfirmedState with { GpuClockOffsets = null };
        }

        ControlOperationResult resume = await context.Runtime.ResumeExperimentalWritesAsync(
            static () => true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, resume.Status);
        Assert.False(context.Provider.IsExperimentalWritesPaused);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(1, context.Device.GpuClockSetCalls);

        // A confirmed observation needs no rewrite; a retired GPU half is written
        // again by the next request.
        ControlOperationResult next = await context.Runtime.SetGpuClockTuningAsync(
            tuning, OverrideLifetime.Persistent, static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, next.Status);
        Assert.Equal(observationAvailable ? 1 : 2, context.Device.GpuClockSetCalls);
        Assert.Equal(tuning.EffectiveOffsets, context.Device.ConfirmedState.GpuClockOffsets?.Current);
    }

    // Paired preset and GPU write while the offsets are not managed (service start,
    // AC plug-in, wake): the firmware preset write lands, its ACK and the follow-up
    // read are lost, so the NVML step never runs. The experimental port pauses and
    // the user resumes; the production port has no pause and the next observation
    // reconciles. Either way the pending entry must not hold thermal writes.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PairedPresetWrite_LostAckWithUnmanagedGpu_RetiresGpuHalfAndUnblocksThermalWrites(
        bool experimental)
    {
        TestContext context = CreateWritableContext();
        context.Provider.AvailableWhenExperimentalAllowed = experimental;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            GpuOverclock = ExperimentalBladePort.CreateGpuOverclockCapabilities(),
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var tuning = new GpuClockTuningSettings(true, new GpuClockOffsetSettings(125, 500));
        PerformancePreset current = context.Device.ConfirmedState.Performance!.Value;
        PerformancePreset next = current == PerformancePreset.Silent
            ? PerformancePreset.Balanced
            : PerformancePreset.Silent;
        context.Device.PerformanceFailures.Enqueue(
            new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK."));
        context.Device.MutatePerformanceBeforeQueuedFailure = true;
        context.Device.BeforeQueuedPerformanceFailure = () =>
        {
            context.Device.ReadStateFailure = new IOException("Injected lost readback.");
            context.Provider.IsExperimentalWritesPaused = experimental;
        };
        var request = new PerformanceOverride(next, OverrideLifetime.Persistent) { GpuClockTuning = tuning };

        ControlOperationResult write = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            request, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Failed, write.Status);
        Assert.Contains(DeviceSubsystem.Performance, context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(next, context.Device.ConfirmedState.Performance);
        Assert.Equal(0, context.Device.GpuClockSetCalls);
        context.Device.ReadStateFailure = null;

        if (experimental)
        {
            ControlOperationResult resume = await context.Runtime.ResumeExperimentalWritesAsync(
                static () => true, CancellationToken.None);
            Assert.Equal(ControlOperationStatus.Applied, resume.Status);
            Assert.False(context.Provider.IsExperimentalWritesPaused);
        }
        else
        {
            await context.Runtime.ObserveAsync(CancellationToken.None);
        }

        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        // The preset change was this write's own; it is not reported as external.
        Assert.DoesNotContain(DeviceSubsystem.Performance, context.Runtime.GetStatus().ExternalChanges);
        Assert.Null(context.Device.ConfirmedState.GpuClockOffsets);

        // The retired GPU half is re-established by the next apply, and thermal
        // writes are no longer held by the pending entry.
        ControlOperationResult reapply = await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            request, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, reapply.Status);
        Assert.Equal(1, context.Device.GpuClockSetCalls);
        Assert.Equal(tuning.EffectiveOffsets, context.Device.ConfirmedState.GpuClockOffsets?.Current);
        ControlOperationResult fans = await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.Persistent),
            CancellationToken.None);
        Assert.NotEqual(ControlOperationStatus.Blocked, fans.Status);
    }

    [Fact]
    public async Task PairedPresetWrite_LostAckWithoutGpuTuning_StillResumes()
    {
        TestContext context = CreateWritableContext();
        context.Provider.AvailableWhenExperimentalAllowed = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        PerformancePreset current = context.Device.ConfirmedState.Performance!.Value;
        PerformancePreset next = current == PerformancePreset.Silent
            ? PerformancePreset.Balanced
            : PerformancePreset.Silent;
        context.Device.PerformanceFailures.Enqueue(
            new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost preset ACK."));
        context.Device.MutatePerformanceBeforeQueuedFailure = true;
        context.Device.BeforeQueuedPerformanceFailure = () =>
        {
            context.Device.ReadStateFailure = new IOException("Injected lost readback.");
            context.Provider.IsExperimentalWritesPaused = true;
        };

        await context.Runtime.SetPerformanceConfigurationOverrideAsync(
            new PerformanceOverride(next, OverrideLifetime.Persistent), CancellationToken.None);
        context.Device.ReadStateFailure = null;
        ControlOperationResult resume = await context.Runtime.ResumeExperimentalWritesAsync(
            static () => true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, resume.Status);
        Assert.False(context.Provider.IsExperimentalWritesPaused);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
    }
}
