using System.Xml.Linq;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CoolingPadViewModelTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DiscoveryAndSavedIntentNeverWriteAndUncontrolledPadShowsNoMode()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        Assert.False(vm.IsVisible);
        vm.ApplyStatus(new()
        {
            Connected = true,
            ConfirmedTargetRpm = 1500,
            Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Auto, LightingMode = CoolingPadLightingMode.Static },
        });
        Assert.True(vm.IsVisible);
        // The pad runs its own settings, so neither selector claims an OpenBlade mode.
        Assert.Null(vm.FanMode);
        Assert.Null(vm.LightingMode);
        Assert.False(vm.ShowLightingOptions);
        Assert.Equal(TrayText.Get("Connected. The pad is using its own settings."), vm.StatusText);
        // Targets are the confirmed configuration, not a measured speed.
        Assert.Equal(TrayText.Format("{0:N0} RPM", 1500), vm.ConfiguredTargetText);
        vm.ApplyStatus(new() { Connected = true, Active = true, ConfirmedTargetRpm = 1500 });
        Assert.Equal(0, client.Writes);
        Assert.Equal(TrayText.Get("OpenBlade is controlling the cooling pad."), vm.StatusText);
        vm.SetServiceAvailable(false);
        Assert.True(vm.IsVisible);
        Assert.False(vm.CanApply);
        vm.ApplyStatus(new() { Connected = false, Connection = CoolingPadConnectionState.Absent });
        Assert.False(vm.IsVisible);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ControlledPadLoadsSavedModesWithoutWriting()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        vm.ApplyStatus(new()
        {
            Connected = true,
            Active = true,
            Requested = CoolingPadSettings.Default with
            {
                FanMode = CoolingPadFanMode.Auto,
                AutoProfile = CoolingPadAutoProfile.Performance,
                LightingMode = CoolingPadLightingMode.Off,
            },
        });
        Assert.Equal(CoolingPadFanMode.Auto, vm.FanMode!.Value);
        Assert.Equal(CoolingPadAutoProfile.Performance, vm.AutoProfile!.Value);
        Assert.Equal(CoolingPadLightingMode.Off, vm.LightingMode!.Value);
        Assert.True(vm.ShowAutoProfile);
        Assert.True(vm.ShowTemperatureSource);
        Assert.False(vm.ShowCurves);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task EachChangeAppliesImmediatelyAndLeavesUnselectedAreasToThePad()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true });
        vm.FanMode = Choice(vm.FanModes, CoolingPadFanMode.Auto);
        Assert.Equal(1, client.Writes);
        Assert.Equal(1, client.Reads);
        Assert.Equal(CoolingPadFanMode.Auto, client.Submitted!.FanMode);
        Assert.Equal(CoolingPadAutoProfile.Balanced, client.Submitted.AutoProfile);
        Assert.Equal(CoolingPadLightingMode.Keep, client.Submitted.LightingMode);
        vm.AutoProfile = Choice(vm.AutoProfiles, CoolingPadAutoProfile.Quiet);
        Assert.Equal(2, client.Writes);
        Assert.Equal(CoolingPadAutoProfile.Quiet, client.Submitted.AutoProfile);
        vm.LightingMode = Choice(vm.LightingModes, CoolingPadLightingMode.Wave);
        Assert.Equal(3, client.Writes);
        Assert.Equal((CoolingPadFanMode.Auto, CoolingPadLightingMode.Wave), (client.Submitted.FanMode, client.Submitted.LightingMode));
        Assert.False(vm.IsApplying);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OptionChangesWithoutAnySelectedModeDoNotWrite()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true });
        vm.Brightness = 40;
        vm.FixedRpm = 2000;
        vm.PrimaryColor = new RgbColor(1, 2, 3);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task RapidChangesCoalesceIntoOneApplyOfTheLatestValues()
    {
        var client = new PadClient();
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher(), applyDelay: TimeSpan.FromMilliseconds(150), refreshInterval: TimeSpan.FromHours(1));
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        foreach (int rpm in new[] { 1600, 1800, 2000, 2250 })
        {
            vm.FixedRpm = rpm;
        }
        await WaitUntilAsync(() => client.Writes == 1);
        await Task.Delay(300);
        Assert.Equal(1, client.Writes);
        Assert.Equal(2250, client.Submitted!.FixedRpm);
    }

    [Fact]
    public async Task ChangeDuringAnApplyIsAppliedAfterItCompletes()
    {
        var client = new PadClient { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        vm.FixedRpm = 1800;
        Assert.Equal(1, client.Writes);
        Assert.True(vm.IsApplying);
        // Controls stay usable during an apply, like the laptop controls.
        Assert.True(vm.CanApply);
        vm.FixedRpm = 2400;
        Assert.Equal(1, client.Writes);
        client.Gate.SetResult();
        await WaitUntilAsync(() => client.Writes == 2 && !vm.IsBusy);
        Assert.Equal(2400, client.Submitted!.FixedRpm);
        Assert.False(vm.IsApplying);
    }

    [Fact]
    public async Task AppDemandDefaultsOffAndMapsTheOptIn()
    {
        Assert.False(CoolingPadSettings.Default.AllowDemandApi);
        var client = new PadClient();
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        Assert.False(vm.AllowDemandApi);
        Assert.True(vm.ShowDemandApiOption);
        vm.AllowDemandApi = true;
        Assert.Equal(1, client.Writes);
        Assert.True(client.Submitted!.AllowDemandApi);
        vm.FanMode = Choice(vm.FanModes, CoolingPadFanMode.Auto);
        Assert.True(vm.ShowDemandApiOption);
        Assert.True(client.Submitted.AllowDemandApi);
        foreach (CoolingPadFanMode mode in new[] { CoolingPadFanMode.Off })
        {
            vm.FanMode = Choice(vm.FanModes, mode);
            Assert.False(vm.ShowDemandApiOption);
            Assert.True(vm.AllowDemandApi);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedApplyOrReleaseDoesNotCreateAnUncertaintyGate(bool release)
    {
        var client = new PadClient { BlockedResult = true };
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        if (release) { await vm.ReleaseCommand.ExecuteAsync(null); }
        else { vm.FixedRpm = 2000; }
        Assert.True(vm.CanApply);
        Assert.True(vm.CanRelease);
        Assert.False(vm.NeedsCheck);
        Assert.Equal(0, client.Reads);
        Assert.Equal(release ? 0 : 1, client.Writes);
        Assert.Equal(release ? 1 : 0, client.Releases);
        Assert.Equal(release
            ? TrayText.Get("The cooling pad did not accept the change. Check Troubleshooting for the reason.")
            : TrayText.Get("The cooling pad has not accepted the change yet. OpenBlade will try again."), vm.StatusText);
    }

    [Fact]
    public async Task RefusedChangeIsRetriedAndTheMessageClearsOnceTheServiceAcceptsIt()
    {
        var client = new PadClient { BlockedResult = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher(), applyDelay: TimeSpan.Zero,
            refreshInterval: TimeSpan.FromHours(1), retryDelay: TimeSpan.FromMilliseconds(20));
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        vm.FixedRpm = 2000;
        Assert.Equal(1, client.Writes);
        // The refused draft stays in the editor while the retry waits.
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        Assert.Equal(2000, vm.FixedRpm);
        client.BlockedResult = false;
        await WaitUntilAsync(() => client.Writes == 2 && !vm.IsBusy);
        Assert.Equal(2000, client.Submitted!.FixedRpm);
        Assert.Equal(TrayText.Get("OpenBlade is controlling the cooling pad."), vm.StatusText);
        await Task.Delay(100);
        Assert.Equal(2, client.Writes);
    }

    [Fact]
    public async Task ReleasingControlCancelsARefusedChangeWaitingToBeRetried()
    {
        var client = new PadClient { BlockedResult = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher(), applyDelay: TimeSpan.Zero,
            refreshInterval: TimeSpan.FromHours(1), retryDelay: TimeSpan.FromMilliseconds(150));
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        vm.FixedRpm = 2000;
        Assert.Equal(1, client.Writes);
        Assert.Equal(TrayText.Get("The cooling pad has not accepted the change yet. OpenBlade will try again."), vm.StatusText);

        client.BlockedResult = false;
        client.NextStatus = new() { Connected = true, Active = false };
        await vm.ReleaseCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Releases);
        await Task.Delay(400);
        Assert.Equal(1, client.Writes);
        Assert.NotEqual(TrayText.Get("The cooling pad has not accepted the change yet. OpenBlade will try again."), vm.StatusText);
    }

    [Fact]
    public async Task PersistentRefusalStopsRetryingAndReportsIt()
    {
        var client = new PadClient { BlockedResult = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher(), applyDelay: TimeSpan.Zero,
            refreshInterval: TimeSpan.FromHours(1), retryDelay: TimeSpan.FromMilliseconds(10));
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        vm.FixedRpm = 2000;
        await WaitUntilAsync(() => client.Writes == 1 + CoolingPadViewModel.MaxRefusedRetries && !vm.IsBusy);
        await Task.Delay(100);
        Assert.Equal(1 + CoolingPadViewModel.MaxRefusedRetries, client.Writes);
        Assert.Equal(TrayText.Get("The cooling pad did not accept the change. Check Troubleshooting for the reason."), vm.StatusText);
        Assert.False(vm.NeedsCheck);
        // A new change gets its own retries.
        vm.FixedRpm = 2400;
        await WaitUntilAsync(() => client.Writes == 2 * (1 + CoolingPadViewModel.MaxRefusedRetries) && !vm.IsBusy);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Unavailable)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    [InlineData(CoolingPadConnectionState.ObservationFailed)]
    public async Task UnknownDiscoveryRetainsConfirmedPageAndTargetsUntilConfirmedAbsent(CoolingPadConnectionState connection)
    {
        await using var vm = Create(new PadClient());
        vm.ApplyStatus(new() { Connection = connection });
        Assert.False(vm.IsVisible);
        vm.ApplyStatus(new() { Connection = CoolingPadConnectionState.PresentExact, ConfirmedTargetRpm = 2100 });
        Assert.True(vm.IsVisible);
        Assert.True(vm.CanApply);
        vm.ApplyStatus(new() { Connection = connection });
        Assert.True(vm.IsVisible);
        Assert.Equal(TrayText.Format("{0:N0} RPM", 2100), vm.ConfiguredTargetText);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanRelease);
        Assert.True(vm.CanRefresh);
        vm.ApplyStatus(new() { Connection = CoolingPadConnectionState.Absent });
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task UncertainSubmissionStopsWritesUntilTheExplicitCheck()
    {
        var client = new PadClient { FailWrite = true };
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        vm.FixedRpm = 2000;
        Assert.Equal(1, client.Writes);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanRelease);
        Assert.True(vm.NeedsCheck);
        Assert.False(vm.ShowRelease);
        Assert.Equal(TrayText.Get("A change to the cooling pad could not be confirmed. OpenBlade is checking the pad."), vm.StatusText);
        vm.FixedRpm = 2200;
        Assert.Equal(1, client.Writes);
        // An unknown read result keeps the gate.
        client.NextStatus = new CoolingPadStatusSnapshot();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.CanApply);
        client.NextStatus = new() { Connection = CoolingPadConnectionState.PresentExact, Active = true };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.CanApply);
        Assert.False(vm.NeedsCheck);
        Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task FailedResultDoesNotAutomaticallyClearWithARecoveryRead()
    {
        var client = new PadClient { FailedResult = true };
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true });
        vm.FanMode = Choice(vm.FanModes, CoolingPadFanMode.Fixed);
        Assert.Equal(1, client.Writes);
        Assert.Equal(0, client.Reads);
        Assert.False(vm.CanApply);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Reads);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task AutomaticRefreshRunsOnlyWhileVisibleAndClearsAnUnconfirmedChangeWithoutWriting()
    {
        var fixedSpeed = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed };
        var client = new PadClient { NextStatus = new() { Connected = true, Active = true, ConfirmedTargetRpm = 1500, Requested = fixedSpeed } };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher(), applyDelay: TimeSpan.Zero, refreshInterval: TimeSpan.FromMilliseconds(20));
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = fixedSpeed });
        await Task.Delay(100);
        Assert.Equal(0, client.Reads);
        vm.IsPageVisible = true;
        await WaitUntilAsync(() => client.Reads >= 3);
        Assert.Equal(0, client.Writes);
        vm.IsPageVisible = false;
        await Task.Delay(60);
        int stopped = client.Reads;
        await Task.Delay(120);
        Assert.Equal(stopped, client.Reads);

        client.FailWrite = true;
        vm.FixedRpm = 2000;
        Assert.True(vm.NeedsCheck);
        Assert.False(vm.CanApply);
        int beforeGate = client.Reads;
        // An unknown result keeps the gate through automatic reads.
        client.NextStatus = new CoolingPadStatusSnapshot();
        vm.IsPageVisible = true;
        await WaitUntilAsync(() => client.Reads >= beforeGate + 2);
        Assert.True(vm.NeedsCheck);
        // A confirmed automatic read clears it; the uncertain change is not sent again.
        client.FailWrite = false;
        client.NextStatus = new() { Connected = true, Active = true, ConfirmedTargetRpm = 1500, Requested = fixedSpeed };
        await WaitUntilAsync(() => !vm.NeedsCheck);
        vm.IsPageVisible = false;
        Assert.True(vm.CanApply);
        Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task AutomaticReadFailureKeepsTheConfirmedStateAndNeedsNoCheck()
    {
        var client = new PadClient { FailRead = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher(), applyDelay: TimeSpan.Zero, refreshInterval: TimeSpan.FromMilliseconds(20));
        vm.ApplyStatus(new() { Connected = true, ConfirmedTargetRpm = 2100 });
        vm.IsPageVisible = true;
        await WaitUntilAsync(() => client.Reads >= 2);
        vm.IsPageVisible = false;
        Assert.True(vm.IsVisible);
        Assert.False(vm.NeedsCheck);
        Assert.True(vm.CanApply);
        Assert.Equal(TrayText.Format("{0:N0} RPM", 2100), vm.ConfiguredTargetText);
    }

    [Fact]
    public async Task ReleaseReturnsTheSelectorsToThePadWithoutWriting()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        vm.ApplyStatus(new()
        {
            Connected = true,
            Active = true,
            Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed, LightingMode = CoolingPadLightingMode.Off },
        });
        client.NextStatus = new() { Connected = true, Active = false };
        await vm.ReleaseCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Releases);
        Assert.Null(vm.FanMode);
        Assert.Null(vm.LightingMode);
        Assert.Equal(0, client.Writes);
        Assert.False(vm.CanRelease);
        // The kept option values return with the next selection.
        vm.FanMode = Choice(vm.FanModes, CoolingPadFanMode.Fixed);
        Assert.Equal(1, client.Writes);
        Assert.Equal(CoolingPadLightingMode.Keep, client.Submitted!.LightingMode);
    }

    [Fact]
    public async Task TransientReadFailurePreservesConfirmedCardAndRecoveryCanRelease()
    {
        var client = new PadClient { FailRead = true };
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true, ConfirmedTargetRpm = 2100 });
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsVisible);
        Assert.Equal(TrayText.Format("{0:N0} RPM", 2100), vm.ConfiguredTargetText);
        vm.ApplyStatus(new() { Connected = true, RecoveryRequired = true });
        Assert.True(vm.CanApply);
        Assert.True(vm.CanRelease);
        client.FailRead = false;
        await vm.ReleaseCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Releases);
    }

    [Fact]
    public async Task CurveEditsUseTheSharedEditorClampBetweenNeighboursAndApply()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.HostCurve } });
        Assert.True(vm.ShowCurves);
        FanCurveEditorPoint point = vm.CpuCurve[1];
        Assert.Equal((500, 3200, 50), (point.MinimumRpm, point.MaximumRpm, point.StepRpm));
        Assert.True(vm.OwnsCurvePoint(point));
        Assert.False(vm.OwnsCurvePoint(new FanCurveEditorPoint(new FanCurvePoint(50, 2000))));
        int upper = vm.CpuCurve[2].SpeedRpm;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        vm.UpdateCurvePoint(point, CoolingPadSettings.MaximumRpm);
        Assert.Contains(nameof(CoolingPadViewModel.CpuCurve), changed);
        Assert.Equal(upper, vm.CpuCurve[1].SpeedRpm);
        Assert.Equal(1, client.Writes);
        Assert.Equal(upper, client.Submitted!.CpuCurve[1].Rpm);
        vm.UpdateCurvePoint(vm.GpuCurve[0], 400);
        Assert.Equal(CoolingPadSettings.MinimumRpm, vm.GpuCurve[0].SpeedRpm);
        Assert.Equal(2, client.Writes);
        Assert.True(CoolingPadSettings.IsWellFormed(client.Submitted));
        Assert.Throws<ArgumentException>(() => vm.UpdateCurvePoint(new FanCurveEditorPoint(new FanCurvePoint(50, 2000)), 2000));
    }

    [Fact]
    public async Task TemperatureSourceSelectsTheVisibleCurves()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.HostCurve } });
        vm.TemperatureSource = Choice(vm.TemperatureSources, CoolingPadTemperatureSource.Cpu);
        Assert.True(vm.ShowCpuCurve);
        Assert.False(vm.ShowGpuCurve);
        Assert.Equal(CoolingPadTemperatureSource.Cpu, client.Submitted!.TemperatureSource);
        vm.TemperatureSource = Choice(vm.TemperatureSources, CoolingPadTemperatureSource.Both);
        Assert.True(vm.ShowCpuCurve && vm.ShowGpuCurve);
    }

    [Fact]
    public async Task TwoWayEditorSettersDoNotRepublishEqualValues()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        int notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        vm.FanMode = vm.FanMode;
        vm.AutoProfile = vm.AutoProfile;
        vm.LightingMode = vm.LightingMode;
        vm.WaveDirection = vm.WaveDirection;
        vm.TemperatureSource = vm.TemperatureSource;
        vm.FixedRpm = vm.FixedRpm;
        vm.FixedRpm = vm.FixedRpm + 10;
        vm.Brightness = vm.Brightness;
        vm.AllowDemandApi = vm.AllowDemandApi;
        vm.PrimaryColor = vm.PrimaryColor;
        vm.SecondaryColor = vm.SecondaryColor;
        Assert.Equal(0, notifications);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task PickedColorsAndSliderValuesReachTheSubmittedSettings()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        vm.ApplyStatus(new() { Connected = true });
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        vm.FanMode = Choice(vm.FanModes, CoolingPadFanMode.Fixed);
        vm.LightingMode = Choice(vm.LightingModes, CoolingPadLightingMode.Breathing);
        vm.PrimaryColor = new RgbColor(255, 64, 0);
        vm.SecondaryColor = new RgbColor(0, 32, 255);
        vm.FixedRpm = 2210;
        vm.Brightness = 40.4;
        // The slider value labels are computed, so each needs its own notification.
        Assert.Contains(nameof(CoolingPadViewModel.FixedRpmText), changed);
        Assert.Contains(nameof(CoolingPadViewModel.BrightnessText), changed);
        Assert.Contains(nameof(CoolingPadViewModel.IsApplying), changed);
        Assert.Equal(TrayText.Format("{0:N0} RPM", 2200), vm.FixedRpmText);
        Assert.Equal(TrayText.Format("{0}%", 40), vm.BrightnessText);
        Assert.Equal(new CoolingPadColor(255, 64, 0), client.Submitted!.Color);
        Assert.Equal(new CoolingPadColor(0, 32, 255), client.Submitted.SecondaryColor);
        Assert.Equal((2200, 40), (client.Submitted.FixedRpm, client.Submitted.BrightnessPercent));
    }

    [Fact]
    public void PageAppliesInlineReusesTheLaptopCurveEditorAndHasNoDisclaimers()
    {
        XDocument document = CanonicalLocalizedXaml.Load("MainWindow.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
        XElement section = Named("CoolingPadSection");
        Assert.Equal("{x:Bind ViewModel.CoolingPad.IsVisible, Mode=OneWay}", (string?)section.Attribute("Visibility"));
        Assert.Contains(Named("CoolingPadPage"), section.Ancestors());
        Assert.DoesNotContain(Named("LaptopPage"), section.Ancestors());
        XElement navigation = Named("CoolingPadNavigationItem");
        string search = File.ReadAllText(Path.Combine(CanonicalLocalizedXaml.RepositoryRoot(), "src", "OpenBlade.Tray", "MainWindow.GlobalSearchCatalog.cs"));
        Assert.Contains("Page(CoolingPadNavigationItem, CoolingPadPage", search, StringComparison.Ordinal);
        Assert.Contains("anchor: CoolingPadCurves, exposed: () => ViewModel.CoolingPad.ShowCurves", search, StringComparison.Ordinal);
        string snapshots = File.ReadAllText(Path.Combine(CanonicalLocalizedXaml.RepositoryRoot(), "src", "OpenBlade.Tray", "MainWindow.CoolingPadSnapshots.cs"));
        Assert.Contains("Confirmed absence must remove cached cooling-pad search results.", snapshots, StringComparison.Ordinal);
        Assert.Equal("cooling-pad", (string?)navigation.Attribute("Tag"));
        Assert.Equal("{x:Bind ViewModel.CoolingPad.IsVisible, Mode=OneWay}", (string?)navigation.Attribute("Visibility"));
        Assert.DoesNotContain(PageLayoutPreferenceDefaults.Layouts.SelectMany(layout => layout.Sections),
            entry => entry.Kind.ToString() == "LaptopCoolingPad");

        // Changes apply as they are made: no Apply or Refresh command, only Release
        // and, after an unconfirmed change, the explicit check that replaces it.
        string[] buttons = section.Descendants().Where(e => e.Name.LocalName == "Button" && e.Attribute("Command") is not null)
            .Select(e => (string)e.Attribute(x + "Name")!).ToArray();
        Assert.Equal(new[] { "CoolingPadRelease", "CoolingPadCheck" }, buttons);
        Assert.Equal("{x:Bind ViewModel.CoolingPad.ShowRelease, Mode=OneWay}", (string?)Named("CoolingPadRelease").Attribute("Visibility"));
        Assert.Equal("{x:Bind ViewModel.CoolingPad.NeedsCheck, Mode=OneWay}", (string?)Named("CoolingPadCheck").Attribute("Visibility"));
        Assert.Equal("{x:Bind ViewModel.CoolingPad.RefreshCommand}", (string?)Named("CoolingPadCheck").Attribute("Command"));
        foreach (string ring in new[] { "CoolingPadFanProgress", "CoolingPadLightingProgress" })
        {
            XElement progress = Named(ring);
            Assert.Equal("ProgressRing", progress.Name.LocalName);
            Assert.Equal("{x:Bind ViewModel.CoolingPad.IsApplying, Mode=OneWay}", (string?)progress.Attribute("IsActive"));
            Assert.NotNull(progress.Attribute("AutomationProperties.Name"));
        }
        string text = section.ToString();
        foreach (string retired in new[] { "Experimental", "CoolingPadApply", "CoolingPadRefresh", "NumberBox", "TextBox" })
        {
            Assert.DoesNotContain(retired, text, StringComparison.Ordinal);
        }

        // Both curve editors use the laptop's point template.
        XElement shared = document.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "FanCurvePointTemplate");
        Assert.Equal("local:FanCurveEditorPoint", (string?)shared.Attribute(x + "DataType"));
        Assert.DoesNotContain(document.Descendants(), e => (string?)e.Attribute(x + "Key") == "CoolingPadCurvePointTemplate");
        XElement[] padCurves = Named("CoolingPadCurves").Descendants().Where(e => e.Name.LocalName == "ItemsControl").ToArray();
        Assert.Equal(2, padCurves.Length);
        Assert.All(padCurves, items => Assert.Equal("{StaticResource FanCurvePointTemplate}", (string?)items.Attribute("ItemTemplate")));
        Assert.Equal(new[] { "{x:Bind ViewModel.CoolingPad.CpuCurve, Mode=OneWay}", "{x:Bind ViewModel.CoolingPad.GpuCurve, Mode=OneWay}" },
            padCurves.Select(items => (string?)items.Attribute("ItemsSource")));
        Assert.True(document.Descendants().Count(e => (string?)e.Attribute("ItemTemplate") == "{StaticResource FanCurvePointTemplate}") >= 4);

        // Only the two color swatches paint a background; shared row styles own every other surface.
        Assert.Equal(new[] { "CoolingPadPrimaryColorSwatch", "CoolingPadSecondaryColorSwatch" },
            section.Descendants().Where(e => e.Attribute("Background") is not null).Select(e => (string?)e.Attribute(x + "Name")));
        Assert.DoesNotContain(section.Descendants(), e => e.Attribute("FontSize") is not null);
        XElement fixedRpm = Named("CoolingPadFixedRpm");
        Assert.Equal("Slider", fixedRpm.Name.LocalName);
        Assert.Equal(("500", "3200", "50", "StepValues"), ((string?)fixedRpm.Attribute("Minimum"), (string?)fixedRpm.Attribute("Maximum"),
            (string?)fixedRpm.Attribute("StepFrequency"), (string?)fixedRpm.Attribute("SnapsTo")));
        Assert.Equal("{Binding CoolingPad.FixedRpm, Mode=TwoWay, Converter={StaticResource Int32Double}}", (string?)fixedRpm.Attribute("Value"));
        XElement brightness = Named("CoolingPadBrightness");
        Assert.Equal(("Slider", "0", "100"), (brightness.Name.LocalName, (string?)brightness.Attribute("Minimum"), (string?)brightness.Attribute("Maximum")));
        foreach (string color in new[] { "CoolingPadPrimaryColor", "CoolingPadSecondaryColor" })
        {
            XElement picker = Assert.Single(Named(color).Descendants(), e => e.Name.LocalName == "ColorPicker");
            Assert.Equal("False", (string?)picker.Attribute("IsAlphaEnabled"));
            // Flyout content does not inherit DataContext, so the picker names its own.
            Assert.Equal("{x:Bind ViewModel.CoolingPad, Mode=OneWay}", (string?)picker.Attribute("DataContext"));
            Assert.Equal("{Binding " + color["CoolingPad".Length..] + ", Mode=TwoWay, Converter={StaticResource RgbColor}}",
                (string?)picker.Attribute("Color"));
        }
        // x:Bind resolves converters from the Window root, which has no Resources;
        // the lookup throws during the first Bindings.Update and crashes startup.
        Assert.DoesNotContain(document.Descendants().SelectMany(e => e.Attributes()),
            a => a.Value.StartsWith("{x:Bind", StringComparison.Ordinal) && a.Value.Contains("Converter=", StringComparison.Ordinal));
        Assert.DoesNotContain("MeasuredRpm", text, StringComparison.Ordinal);
        foreach ((string name, string selection) in new[]
                 {
                     ("CoolingPadFanSelector", "FanMode"),
                     ("CoolingPadAutoProfile", "AutoProfile"),
                     ("CoolingPadLightingSelector", "LightingMode"),
                     ("CoolingPadTemperatureSource", "TemperatureSource"),
                     ("CoolingPadWaveDirection", "WaveDirection"),
                 })
        {
            XElement selector = Named(name);
            Assert.Equal("ResponsiveChoiceSelector", selector.Name.LocalName);
            Assert.Equal("{x:Bind ViewModel.CoolingPad." + selection + ", Mode=TwoWay}", (string?)selector.Attribute("SelectedItem"));
            Assert.Equal("{x:Bind ViewModel.CoolingPad.CanApply, Mode=OneWay}", (string?)selector.Attribute("IsEnabled"));
            Assert.Equal("Label", (string?)selector.Attribute("DisplayMemberPath"));
            Assert.NotNull(selector.Attribute("AccessibleName"));
            Assert.Equal("150", (string?)selector.Attribute("CompactMinWidth"));
            Assert.Equal("800", (string?)selector.Attribute("ExpandedMinWindowWidth"));
        }
        // Each section opens with the same first row as the laptop's Fans card.
        XElement reference = Named("DashboardFanModeSelector").Ancestors().First(e => e.Name.LocalName == "Border");
        foreach (string label in new[] { "CoolingPadFanLabel", "CoolingPadLightingLabel" })
        {
            XElement title = Named(label);
            XElement row = title.Ancestors().First(e => e.Name.LocalName == "Border");
            Assert.Equal((string?)reference.Attribute("Style"), (string?)row.Attribute("Style"));
            Assert.Equal((string?)reference.Attribute("Padding"), (string?)row.Attribute("Padding"));
            Assert.Equal("{StaticResource DashboardSingleLineRowContentMinHeight}", (string?)title.Parent!.Attribute("MinHeight"));
            Assert.Equal("OnDashboardSingleLineTitleSizeChanged", (string?)title.Attribute("SizeChanged"));
        }
    }

    [Fact]
    public void CurveCommitsRouteToThePadBeforeTheLaptopFanControl()
    {
        string code = File.ReadAllText(Path.Combine(CanonicalLocalizedXaml.RepositoryRoot(), "src", "OpenBlade.Tray", "MainWindow.xaml.cs"));
        int start = code.IndexOf("private async Task CommitFanCurvePointAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string body = code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];
        int pad = body.IndexOf("ViewModel.CoolingPad.OwnsCurvePoint(point)", StringComparison.Ordinal);
        int laptop = body.IndexOf("ViewModel.UpdateFanCurvePoint(point, speedRpm)", StringComparison.Ordinal);
        Assert.True(pad >= 0 && laptop > pad);
        int update = body.IndexOf("ViewModel.CoolingPad.UpdateCurvePoint(point, speedRpm);", StringComparison.Ordinal);
        Assert.True(update > pad && body.IndexOf("return;", update, StringComparison.Ordinal) is int exit && exit > update && exit < laptop);
        Assert.Contains("ViewModel.CoolingPad.IsPageVisible =", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCoolingPadResourceReferencedFromCodeExists()
    {
        // Search and snapshots look keys up at runtime; a retired key throws there.
        string root = CanonicalLocalizedXaml.RepositoryRoot();
        string catalog = File.ReadAllText(Path.Combine(root, "src", "OpenBlade.Tray", "Strings", "en-US", "Resources.resw"));
        string[] keys = Directory.EnumerateFiles(Path.Combine(root, "src", "OpenBlade.Tray"), "*.cs")
            .SelectMany(file => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "\"(MainWindow_CoolingPad[A-Za-z]+\\.[A-Za-z]+)\"")
                .Select(match => match.Groups[1].Value))
            .Distinct().ToArray();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Contains("<data name=\"" + key + "\"", catalog, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestChangedOutsideThePageReloadsTheEditorWithoutWriting()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        var quietCpu = CoolingPadSettings.Default with
        {
            FanMode = CoolingPadFanMode.Auto,
            AutoProfile = CoolingPadAutoProfile.Quiet,
            TemperatureSource = CoolingPadTemperatureSource.Cpu,
        };
        vm.ApplyStatus(new() { Connected = true, Requested = quietCpu });
        Assert.Null(vm.FanMode);

        // Control started by another client: the page shows what the service runs.
        var balancedBoth = quietCpu with { AutoProfile = CoolingPadAutoProfile.Balanced, TemperatureSource = CoolingPadTemperatureSource.Both };
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = balancedBoth });
        Assert.Equal(CoolingPadFanMode.Auto, vm.FanMode?.Value);
        Assert.Equal(CoolingPadAutoProfile.Balanced, vm.AutoProfile?.Value);
        Assert.Equal(CoolingPadTemperatureSource.Both, vm.TemperatureSource?.Value);

        // A curve-only change is a different request too.
        var curve = balancedBoth with { FanMode = CoolingPadFanMode.HostCurve, CpuCurve = [new(40, 600), new(90, 2400)] };
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = curve });
        Assert.Equal(CoolingPadFanMode.HostCurve, vm.FanMode?.Value);
        Assert.Equal([600, 2400], vm.CpuCurve.Select(point => point.SpeedRpm));

        // Control ending elsewhere returns the selectors to the pad.
        vm.ApplyStatus(new() { Connected = true, Requested = curve });
        Assert.Null(vm.FanMode);
        await Task.Delay(50);
        Assert.Equal(0, client.Writes);

        // The next choice sends the request the service last ran.
        vm.FanMode = Choice(vm.FanModes, CoolingPadFanMode.Auto);
        Assert.Equal(CoolingPadTemperatureSource.Both, client.Submitted!.TemperatureSource);
        Assert.Equal(CoolingPadAutoProfile.Balanced, client.Submitted.AutoProfile);
    }

    [Fact]
    public async Task LightingKeptOnThePadShowsNoLightingSoChoosingTheSavedOneAppliesIt()
    {
        var client = new PadClient();
        await using var vm = Create(client);
        var autoOff = CoolingPadSettings.Default with
        {
            FanMode = CoolingPadFanMode.Auto,
            AutoProfile = CoolingPadAutoProfile.Quiet,
            LightingMode = CoolingPadLightingMode.Off,
        };
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = autoOff });
        Assert.Equal(CoolingPadLightingMode.Off, vm.LightingMode?.Value);

        // The pad's lighting button changed the lighting; the fan stays controlled.
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = autoOff, LightingKept = true });
        Assert.Equal(CoolingPadFanMode.Auto, vm.FanMode?.Value);
        Assert.Null(vm.LightingMode);
        await Task.Delay(50);
        Assert.Equal(0, client.Writes);

        // Choosing the saved lighting again is a change, so it is applied.
        vm.LightingMode = Choice(vm.LightingModes, CoolingPadLightingMode.Off);
        Assert.Equal(1, client.Writes);
        Assert.Equal((CoolingPadFanMode.Auto, CoolingPadLightingMode.Off), (client.Submitted!.FanMode, client.Submitted.LightingMode));
        Assert.Equal(CoolingPadLightingMode.Off, vm.LightingMode?.Value);
        // The service manages lighting again and reports it; the editor keeps the choice.
        vm.ApplyStatus(client.NextStatus with { Requested = client.Submitted });
        Assert.Equal(CoolingPadLightingMode.Off, vm.LightingMode?.Value);
        Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task ALocalChangeWaitingToApplyWinsOverAnOlderStatus()
    {
        var client = new PadClient();
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher(), applyDelay: TimeSpan.FromMilliseconds(150), refreshInterval: TimeSpan.FromHours(1));
        var fixedSpeed = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed };
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = fixedSpeed });
        vm.FixedRpm = 2000;
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = fixedSpeed });
        Assert.Equal(2000, vm.FixedRpm);
        await WaitUntilAsync(() => client.Writes == 1 && !vm.IsBusy);
        Assert.Equal(2000, client.Submitted!.FixedRpm);
        // The applied request now matches the service, so later statuses keep it.
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = client.Submitted });
        Assert.Equal(2000, vm.FixedRpm);
    }

    [Fact]
    public async Task AnApplyInFlightWinsOverAnOlderStatus()
    {
        var client = new PadClient { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = Create(client);
        var fixedSpeed = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed };
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = fixedSpeed });
        vm.FixedRpm = 1800;
        Assert.True(vm.IsApplying);
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = fixedSpeed });
        Assert.Equal(1800, vm.FixedRpm);
        client.Gate.SetResult();
        await WaitUntilAsync(() => !vm.IsBusy);
        Assert.Equal(1800, vm.FixedRpm);
        Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task ARefusedChangeReturnsTheEditorToTheServiceRequestOnceItsRetriesEnd()
    {
        var client = new PadClient { BlockedResult = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher(), applyDelay: TimeSpan.Zero,
            refreshInterval: TimeSpan.FromHours(1), retryDelay: TimeSpan.FromMilliseconds(10));
        var fixedSpeed = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed };
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = fixedSpeed });
        vm.FixedRpm = 2400;
        await WaitUntilAsync(() => client.Writes == 1 + CoolingPadViewModel.MaxRefusedRetries && !vm.IsBusy);
        vm.ApplyStatus(new() { Connected = true, Active = true, Requested = fixedSpeed });
        Assert.Equal(fixedSpeed.FixedRpm, vm.FixedRpm);
        Assert.Equal(1 + CoolingPadViewModel.MaxRefusedRetries, client.Writes);
    }

    private static CoolingPadViewModel Create(PadClient client) =>
        new(client, new InlineDispatcher(), applyDelay: TimeSpan.Zero, refreshInterval: TimeSpan.FromHours(1));

    private static CoolingPadChoice<T> Choice<T>(IReadOnlyList<CoolingPadChoice<T>> choices, T value) =>
        choices.Single(choice => EqualityComparer<T>.Default.Equals(choice.Value, value));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "Timed out waiting for the cooling-pad view model.");
            await Task.Delay(10);
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken token) { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }

    private sealed class PadClient : OpenBlade.Testing.UnavailableServiceClient, IServiceClient
    {
        private int writes;
        private int reads;
        private int releases;
        public int Writes => Volatile.Read(ref writes);
        public int Reads => Volatile.Read(ref reads);
        public int Releases => Volatile.Read(ref releases);
        public bool FailWrite { get; set; }
        public bool FailedResult { get; init; }
        public bool BlockedResult { get; set; }
        public bool FailRead { get; set; }
        public TaskCompletionSource? Gate { get; init; }
        public CoolingPadSettings? Submitted { get; private set; }
        public CoolingPadStatusSnapshot NextStatus { get; set; } = new() { Connected = true, Active = true, ConfirmedTargetRpm = 1500 };
        public async Task<ControlOperationResult> SetCoolingPadSettingsAsync(CoolingPadSettings settings, CancellationToken token)
        {
            Interlocked.Increment(ref writes); Submitted = settings;
            if (Gate is { } gate && Writes == 1) { await gate.Task.WaitAsync(token); }
            if (FailWrite) { throw new IOException("submission uncertain"); }
            return new ControlOperationResult(BlockedResult ? ControlOperationStatus.Blocked : FailedResult ? ControlOperationStatus.Failed : ControlOperationStatus.Applied, "Applied");
        }
        public Task<ControlOperationResult> ReleaseCoolingPadControlAsync(CancellationToken token)
        {
            Interlocked.Increment(ref releases);
            return Task.FromResult(new ControlOperationResult(BlockedResult ? ControlOperationStatus.Blocked : ControlOperationStatus.Applied, "Released"));
        }
        public Task<ServiceStatusSnapshot> ReadCoolingPadStatusAsync(CancellationToken token)
        {
            Interlocked.Increment(ref reads);
            if (FailRead) { throw new IOException("temporarily unavailable"); }
            ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
            return Task.FromResult(new ServiceStatusSnapshot(1, true, false, [], null, [], "Ready", null, null,
                profiles, new DesiredDeviceState(
                    new(profiles.PluggedIn.Performance, SettingValueSource.PowerProfile),
                    new(profiles.PluggedIn.Fans, SettingValueSource.PowerProfile),
                    new(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                    new(profiles.BatteryPolicy, true, false)), PowerSource.PluggedIn, null, null, null, null, false)
            // The service stores an accepted request exactly as it was sent.
            { CoolingPad = Submitted is { } submitted && !BlockedResult ? NextStatus with { Requested = submitted } : NextStatus });
        }
    }
}
