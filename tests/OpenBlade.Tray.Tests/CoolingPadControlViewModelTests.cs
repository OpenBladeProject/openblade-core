using OpenBlade.Contracts;
using OpenBlade.Testing;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CoolingPadControlViewModelTests
{
    [Fact]
    public async Task HostCurveRequestPlannedDemandAndHistoricalAcknowledgementHaveSeparateLabels()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.SelectedMode = view.ModeOptions.Single(p => p.Mode == CoolingPadControlMode.HostCurve);
        view.Update(Snapshot(Pad() with
        {
            Requested = view.CreateSettings(),
            PlannedDemand = new(1350),
            LastAcknowledgedDemand = new(1600),
        }), true);
        Assert.Contains("Host curve", view.RequestedText);
        Assert.DoesNotContain("1350", view.RequestedText);
        Assert.Contains("1350", view.PlannedText);
        Assert.Contains("1600", view.AcknowledgedText);
    }

    [Fact]
    public async Task ReconnectAcceptsNewServiceRevisionEpoch()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad()) with { Revision = 100 }, true);
        view.Disconnect();
        view.Update(Snapshot(Pad() with { LastAcknowledgedDemand = new(2200) }) with { Revision = 1 }, true);
        Assert.True(view.IsConnected);
        Assert.Contains("2200", view.AcknowledgedText);
        Assert.True(view.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditorBuildsIndependentImmutableCurvesAndRejectsInvalidDraft()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        Assert.Equal(CoolingPadControlSettings.Stopped, view.CreateSettings());
        view.SelectedMode = view.ModeOptions.Single(p => p.Mode == CoolingPadControlMode.HostCurve);
        var settings = view.CreateSettings();
        Assert.Equal(40, settings.CpuCurve!.Points[0].TemperatureCelsius);
        Assert.Equal(30, settings.GpuCurve!.Points[0].TemperatureCelsius);
        view.CpuPoints[0].Demand = 1500;
        Assert.Equal(1000, settings.CpuCurve.Points[0].Demand.Rpm);
        Assert.Equal(1000, view.CreateSettings().GpuCurve!.Points[0].Demand.Rpm);
        view.GpuPoints[1].Temperature = 20;
        Assert.False(view.IsEditorValid);
        Assert.Throws<ArgumentException>(() => view.CreateSettings());
    }

    [Theory]
    [InlineData(1500, true)]
    [InlineData(1510, false)]
    [InlineData(0, false)]
    public async Task FixedEditorUsesConfiguredDemandValidation(int demand, bool valid)
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.SelectedMode = view.ModeOptions.Single(p => p.Mode == CoolingPadControlMode.FixedDemand);
        view.FixedDemand = demand;
        Assert.Equal(valid, view.IsEditorValid);
    }

    [Fact]
    public async Task ResumeRequiresWritableConnectedKnownIntent()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad() with { Access = DeviceFeatureAccess.ObservedOnly }), true);
        Assert.False(view.ResumeCommand.CanExecute(null));
        view.Update(Snapshot(Pad() with { SubmissionStateUnknown = true }), true);
        Assert.False(view.ResumeCommand.CanExecute(null));
        view.Update(Snapshot(Pad() with { Requested = CoolingPadControlSettings.Stopped }), true);
        Assert.False(view.ResumeCommand.CanExecute(null));
        view.Update(Snapshot(Pad()), true);
        Assert.True(view.ResumeCommand.CanExecute(null));
        view.Disconnect();
        Assert.False(view.ResumeCommand.CanExecute(null));
        Assert.False(view.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task RequestedDemandDoesNotReplaceHistoricalAcknowledgement()
    {
        await using var view = new CoolingPadControlViewModel(new Client(), default);
        view.Update(Snapshot(Pad()), true);
        Assert.Contains("1600", view.RequestedText);
        Assert.Contains("1350", view.AcknowledgedText);
        view.Update(Snapshot(Pad() with { LastAcknowledgedDemand = null }), true);
        Assert.DoesNotContain("1600", view.AcknowledgedText);
        Assert.DoesNotContain("1500", view.AcknowledgedText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMutationIsNotRetriedOrRefreshed(bool resume)
    {
        var client = new Client { Mutation = _ => Task.FromException<ControlOperationResult>(new IOException("lost response")) };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        await (resume ? view.ResumeCommand : view.ApplyCommand).ExecuteAsync(null);
        Assert.Equal(1, client.Mutations);
        Assert.Equal(0, client.Refreshes);
        Assert.NotEmpty(view.Message);
    }

    [Fact]
    public async Task StoredMutationRefreshesAuthoritativeStatusWithoutInferringAcknowledgement()
    {
        var client = new Client { Status = Snapshot(Pad() with { LastAcknowledgedDemand = null }) };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        view.SelectedMode = view.ModeOptions.Single(p => p.Mode == CoolingPadControlMode.FixedDemand);
        view.FixedDemand = 2200;
        await view.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(2200, client.LastSettings!.FixedDemand!.Value.Rpm);
        Assert.Equal(1, client.Mutations);
        Assert.Equal(1, client.Refreshes);
        Assert.Contains("1600", view.RequestedText);
        Assert.DoesNotContain("2200", view.AcknowledgedText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAndResumeCannotOverlap(bool resumeFirst)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ControlOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Mutation = _ => { entered.TrySetResult(); return release.Task; } };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        var first = resumeFirst ? view.ResumeCommand : view.ApplyCommand;
        var second = resumeFirst ? view.ApplyCommand : view.ResumeCommand;
        Task running = first.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool secondEnabled = second.CanExecute(null);
        Task overlapping = second.ExecuteAsync(null);
        release.SetResult(new(ControlOperationStatus.Stored, "Stored"));
        await Task.WhenAll(running, overlapping).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondEnabled);
        Assert.Equal(1, client.Mutations);
    }

    [Fact]
    public async Task LateRefreshCannotUndoDisconnect()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Refresh = _ => { entered.TrySetResult(); return release.Task; } };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        Task running = view.ApplyCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Disconnect();
        release.SetResult(Snapshot(Pad()));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(view.IsConnected);
        Assert.False(view.ResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task OlderCommandRefreshCannotReplaceNewerWatchStatus()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ServiceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Refresh = _ => { entered.TrySetResult(); return release.Task; } };
        await using var view = new CoolingPadControlViewModel(client, default);
        view.Update(Snapshot(Pad()), true);
        Task running = view.ApplyCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Update(Snapshot(Pad() with { LastAcknowledgedDemand = new(2200) }) with { Revision = 2 }, true);
        release.SetResult(Snapshot(Pad()));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("2200", view.AcknowledgedText);
    }

    private static CoolingPadControlStatus Pad() => new(
        new(CoolingPadControlMode.FixedDemand, new(1600)), CoolingPadControlState.Paused,
        CoolingPadControlPauseReason.ExplicitResumeRequired, new(1500), new(1350))
    { Access = DeviceFeatureAccess.ReadWrite, ResumeRequired = true };

    private static ServiceStatusSnapshot Snapshot(CoolingPadControlStatus pad)
    {
        var profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(1, false, true, [], null, [], "Test",
            DeviceCapabilities.Unavailable(), null, profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn, null, null, null, null, false)
        { CoolingPadControl = pad };
    }

    private sealed class Client : UnavailableServiceClient, IServiceClient
    {
        public int Mutations { get; private set; }
        public int Refreshes { get; private set; }
        public CoolingPadControlSettings? LastSettings { get; private set; }
        public ServiceStatusSnapshot Status { get; init; } = Snapshot(Pad());
        public Func<CancellationToken, Task<ControlOperationResult>> Mutation { get; init; } =
            _ => Task.FromResult(new ControlOperationResult(ControlOperationStatus.Stored, "Stored"));
        public Func<CancellationToken, Task<ServiceStatusSnapshot>>? Refresh { get; init; }

        public Task<ControlOperationResult> SetCoolingPadControlAsync(CoolingPadControlSettings settings, CancellationToken token)
        {
            LastSettings = settings;
            Mutations++;
            return Mutation(token);
        }

        public Task<ControlOperationResult> ResumeCoolingPadControlAsync(CancellationToken token)
        {
            Mutations++;
            return Mutation(token);
        }

        public override Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
        {
            Refreshes++;
            return Refresh?.Invoke(token) ?? Task.FromResult(Status);
        }
    }
}
