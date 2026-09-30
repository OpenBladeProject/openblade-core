using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerOnlyWireStatusUpdatesExactLimitsForVisibleAndHiddenTray(bool hidden)
    {
        using var language = TrayLocalization.BeginTemporaryLanguageOverride("en-US");
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var backend = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Available);
        var cpu = new CpuTuningStatus(null, new(new(null, 85000, 80000), []),
            backend, backend, CpuTuningPendingBoot.Unknown, false);
        ServiceStatusSnapshot baseline = CreateStatus(true, false) with
        {
            Revision = 4,
            Capabilities = DeviceCapabilities.SafeTargetDefaults() with
            {
                CpuPowerLimit = new(new(45, 95, 1), DeviceFeatureAccess.ObservedOnly),
            },
            CpuTuning = cpu,
        };
        await PushAsync(client, dispatcher, baseline);
        viewModel.SetDashboardTelemetryPresentationActive(!hidden);
        string before = viewModel.CpuTuning.LiveSummary;
        var update = baseline with
        {
            Revision = 5,
            CpuTuning = cpu with { LiveState = new(new(null, 85123, 80765), []) },
        };
        using MemoryStream wire = new();
        await IpcMessageCodec.WriteServerFrameAsync(wire,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, update), CancellationToken.None);
        wire.Position = 0;
        var received = Assert.IsType<ServiceStatusEvent>(await IpcMessageCodec.ReadServerFrameAsync(wire, CancellationToken.None));
        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task observation = hidden ? viewModel.ObserveTrayStateAsync(CancellationToken.None)
            : viewModel.ObserveStateAsync(CancellationToken.None);
        client.PendingObservation.SetResult(received.Status);
        await observation;

        Assert.NotEqual(before, viewModel.CpuTuning.LiveSummary);
        Assert.Contains($"fast {(85123 / 1000m).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)} W", viewModel.CpuTuning.LiveSummary);
        Assert.Contains($"slow {(80765 / 1000m).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)} W", viewModel.CpuTuning.LiveSummary);
        Assert.Contains("CO Unknown", viewModel.CpuTuning.LiveSummary);
        Assert.DoesNotContain("mixed", viewModel.CpuTuning.LiveSummary);
        Assert.False(viewModel.CpuTuning.CanApply);
        Assert.False(viewModel.CpuTuning.CanRestore);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RecoveryOnlyStatusChangeRevokesRestoreForVisibleAndHiddenTray(bool live, bool hidden)
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        var backend = new CpuTuningBackendStatus(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available);
        var recovery = new CpuTuningRecoveryStatus(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer);
        var cpu = new CpuTuningStatus(null, null, backend, backend, CpuTuningPendingBoot.Unknown, true)
        {
            FirmwareRecovery = live ? null : recovery,
            LiveRecovery = live ? recovery : null,
        };
        ServiceStatusSnapshot baseline = CreateStatus(true, false) with
        {
            Revision = 4,
            Capabilities = DeviceCapabilities.SafeTargetDefaults() with
            {
                CpuPowerLimit = new(new(45, 95, 1), DeviceFeatureAccess.ObservedOnly),
                CpuVoltageOptimizer = new(new(-30, -1, 1), DeviceFeatureAccess.ReadWrite),
            },
            CpuTuning = cpu,
        };
        await PushAsync(client, dispatcher, baseline);
        viewModel.CpuTuning.LiveNow = live;
        Assert.True(viewModel.CpuTuning.CanRestore);
        viewModel.SetDashboardTelemetryPresentationActive(!hidden);
        var blocked = recovery with { State = CpuTuningRecoveryState.Blocked };
        ServiceStatusSnapshot update = baseline with
        {
            Revision = 5,
            CpuTuning = live ? cpu with { LiveRecovery = blocked } : cpu with { FirmwareRecovery = blocked },
        };
        client.PendingObservation = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task observation = hidden
            ? viewModel.ObserveTrayStateAsync(CancellationToken.None)
            : viewModel.ObserveStateAsync(CancellationToken.None);
        client.PendingObservation.SetResult(update);
        await observation;

        Assert.False(viewModel.CpuTuning.CanRestore);
    }
}
