using System.Xml.Linq;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CoolingPadViewModelTests
{
    [Fact]
    public async Task AppDemandDefaultsOffAndExplicitApplyMapsThePendingOptIn()
    {
        Assert.False(CoolingPadSettings.Default.AllowDemandApi);
        var client = new PadClient();
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        vm.ApplyStatus(new() { Connected = true, Requested = CoolingPadSettings.Default with { FanMode = CoolingPadFanMode.Fixed } });
        Assert.False(vm.AllowDemandApi);
        Assert.True(vm.ShowDemandApiOption);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.False(client.Submitted!.AllowDemandApi);
        vm.AllowDemandApi = true;
        vm.ApplyStatus(new() { Connected = true, Requested = CoolingPadSettings.Default });
        Assert.True(vm.AllowDemandApi);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(client.Submitted!.AllowDemandApi);
        Assert.Equal(2, client.Writes);
    }

    [Fact]
    public async Task AppDemandHydratesSavedIntentAndKeepsItWhenFanControlsAreHidden()
    {
        var client = new PadClient();
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        vm.ApplyStatus(new()
        {
            Connected = true,
            Requested = CoolingPadSettings.Default with
            {
                FanMode = CoolingPadFanMode.HostCurve,
                AllowDemandApi = true,
            },
        });
        Assert.True(vm.AllowDemandApi);
        Assert.True(vm.ShowDemandApiOption);
        foreach (CoolingPadFanMode mode in new[] { CoolingPadFanMode.Off, CoolingPadFanMode.Keep })
        {
            vm.FanMode = vm.FanModes.Single(option => option.Value == mode);
            Assert.False(vm.ShowDemandApiOption);
            Assert.True(vm.AllowDemandApi);
        }
        vm.FanMode = vm.FanModes.Single(option => option.Value == CoolingPadFanMode.Fixed);
        Assert.True(vm.ShowDemandApiOption);
        vm.ApplyStatus(new() { Connected = true, WritesPaused = true });
        Assert.False(vm.CanApply);
        Assert.True(vm.AllowDemandApi);
        vm.ApplyStatus(new() { Connection = CoolingPadConnectionState.Unavailable });
        Assert.False(vm.CanApply);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedApplyOrReleaseDoesNotCreateAnUncertaintyGate(bool release)
    {
        var client = new PadClient { BlockedResult = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        vm.ApplyStatus(new() { Connected = true, Active = true });
        await (release ? vm.ReleaseCommand : vm.ApplyCommand).ExecuteAsync(null);
        Assert.True(vm.CanApply);
        Assert.True(vm.CanRelease);
        Assert.Equal(0, client.Reads);
        Assert.Equal(release ? 0 : 1, client.Writes);
        Assert.Equal(release ? 1 : 0, client.Releases);
        Assert.Equal(TrayText.Get("The cooling-pad change was blocked. Refresh status and check Troubleshooting."), vm.StatusText);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Unavailable)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    [InlineData(CoolingPadConnectionState.ObservationFailed)]
    public async Task UnknownDiscoveryRetainsConfirmedPageAndTargetsUntilConfirmedAbsent(CoolingPadConnectionState connection)
    {
        await using var vm = new CoolingPadViewModel(new PadClient(), new InlineDispatcher());
        vm.ApplyStatus(new() { Connection = connection });
        Assert.False(vm.IsVisible);
        vm.ApplyStatus(new() { Connection = CoolingPadConnectionState.PresentExact, ConfirmedTargetRpm = 2100 });
        Assert.True(vm.IsVisible);
        Assert.True(vm.CanApply);
        vm.ApplyStatus(new() { Connection = connection });
        Assert.True(vm.IsVisible);
        Assert.Contains("2100", vm.ConfiguredTargetText);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanRelease);
        Assert.True(vm.CanRefresh);
        vm.ApplyStatus(new() { Connection = CoolingPadConnectionState.Absent });
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task UnknownExplicitReadDoesNotClearSubmissionUncertainty()
    {
        var client = new PadClient { FailWrite = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        vm.ApplyStatus(new() { Connected = true });
        await vm.ApplyCommand.ExecuteAsync(null);
        client.NextStatus = new CoolingPadStatusSnapshot();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.ApplyStatus(new() { Connected = true });
        Assert.False(vm.CanApply);
        client.NextStatus = new() { Connection = CoolingPadConnectionState.PresentExact };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task TwoWayEditorSettersDoNotRepublishEqualValues()
    {
        await using var vm = new CoolingPadViewModel(new PadClient(), new InlineDispatcher());
        int notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        vm.FanMode = vm.FanMode;
        vm.LightingMode = vm.LightingMode;
        vm.WaveDirection = vm.WaveDirection;
        vm.TemperatureSource = vm.TemperatureSource;
        vm.FixedRpm = vm.FixedRpm;
        vm.Brightness = vm.Brightness;
        vm.AllowDemandApi = vm.AllowDemandApi;
        vm.PrimaryColor = vm.PrimaryColor;
        vm.SecondaryColor = vm.SecondaryColor;
        Assert.Equal(0, notifications);
        vm.CpuCurve[0].PropertyChanged += (_, _) => notifications++;
        vm.CpuCurve[0].Temperature = vm.CpuCurve[0].Temperature;
        vm.CpuCurve[0].Rpm = vm.CpuCurve[0].Rpm;
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void CardUsesSharedNeutralRowsAndEditorSurfacesWithExplicitRequestedLabels()
    {
        XDocument document = CanonicalLocalizedXaml.Load("MainWindow.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement section = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "CoolingPadSection");
        Assert.Equal("{x:Bind ViewModel.CoolingPad.IsVisible, Mode=OneWay}", (string?)section.Attribute("Visibility"));
        XElement page = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "CoolingPadPage");
        XElement laptop = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "LaptopPage");
        XElement navigation = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "CoolingPadNavigationItem");
        Assert.Contains(page, section.Ancestors());
        Assert.DoesNotContain(laptop, section.Ancestors());
        Assert.Equal("cooling-pad", (string?)navigation.Attribute("Tag"));
        Assert.Equal("{x:Bind ViewModel.CoolingPad.IsVisible, Mode=OneWay}", (string?)navigation.Attribute("Visibility"));
        Assert.DoesNotContain(PageLayoutPreferenceDefaults.Layouts.SelectMany(layout => layout.Sections),
            entry => entry.Kind.ToString() == "LaptopCoolingPad");
        XElement reference = document.Descendants().First(e => (string?)e.Attribute("Style") == "{StaticResource SettingsNeutralSubcardStyle}");
        XElement fan = section.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "CoolingPadFanLabel");
        XElement fanRow = fan.Ancestors().First(e => e.Name.LocalName == "Border");
        Assert.Equal((string?)reference.Attribute("Style"), (string?)fanRow.Attribute("Style"));
        Assert.Equal("{StaticResource DashboardSingleLineRowPadding}", (string?)fanRow.Attribute("Padding"));
        Assert.Equal("{StaticResource DashboardSingleLineRowContentMinHeight}", (string?)fan.Parent!.Attribute("MinHeight"));
        Assert.Equal("OnDashboardSingleLineTitleSizeChanged", (string?)fan.Attribute("SizeChanged"));
        Assert.DoesNotContain(section.Descendants(), e => e.Attribute("FontSize") is not null || e.Attribute("Background") is not null);
        XElement curves = section.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "CoolingPadCurves");
        Assert.Equal("{StaticResource CardContentExpanderStyle}", (string?)curves.Attribute("Style"));
        Assert.Contains(section.Descendants(), e => (string?)e.Attribute("Header") == "Requested fan target (RPM)");
        Assert.DoesNotContain("MeasuredRpm", section.ToString(), StringComparison.Ordinal);
        foreach ((string name, string selection) in new[]
                 {
                     ("CoolingPadFanSelector", "FanMode"),
                     ("CoolingPadLightingSelector", "LightingMode"),
                     ("CoolingPadTemperatureSource", "TemperatureSource"),
                     ("CoolingPadWaveDirection", "WaveDirection"),
                 })
        {
            XElement selector = section.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("ResponsiveChoiceSelector", selector.Name.LocalName);
            Assert.Equal("{x:Bind ViewModel.CoolingPad." + selection + ", Mode=TwoWay}", (string?)selector.Attribute("SelectedItem"));
            Assert.Equal("Label", (string?)selector.Attribute("DisplayMemberPath"));
            Assert.NotNull(selector.Attribute("AccessibleName"));
            Assert.Equal("150", (string?)selector.Attribute("CompactMinWidth"));
            Assert.Equal("800", (string?)selector.Attribute("ExpandedMinWindowWidth"));
        }
    }

    [Fact]
    public async Task DiscoveryAndEditorChangesNeverWriteAndTargetsAreNotMeasuredSpeeds()
    {
        var client = new PadClient();
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        Assert.False(vm.IsVisible);
        vm.ApplyStatus(new() { Connected = true, Identity = "1532:0F47", ConfirmedTargetRpm = 1500 });
        vm.FixedRpm = 2000;
        Assert.True(vm.IsVisible);
        Assert.Equal(0, client.Writes);
        Assert.Contains("1500", vm.ConfiguredTargetText);
        vm.SetServiceAvailable(false);
        Assert.True(vm.IsVisible);
        Assert.False(vm.CanApply);
        Assert.Contains("1500", vm.ConfiguredTargetText);
        vm.ApplyStatus(new() { Connected = false, Connection = CoolingPadConnectionState.Absent });
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task ExplicitApplySubmitsOnceThenReadsWithoutOverwritingUnappliedDraft()
    {
        var client = new PadClient();
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        vm.ApplyStatus(new() { Connected = true });
        vm.FanMode = vm.FanModes.Single(x => x.Value == CoolingPadFanMode.Fixed);
        vm.FixedRpm = 2000;
        vm.TemperatureSource = vm.TemperatureSources.Single(x => x.Value == CoolingPadTemperatureSource.Cpu);
        vm.LightingMode = vm.LightingModes.Single(x => x.Value == CoolingPadLightingMode.Wave);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Writes);
        Assert.Equal(1, client.Reads);
        Assert.Equal(2000, client.Submitted!.FixedRpm);
        Assert.Equal(CoolingPadTemperatureSource.Cpu, client.Submitted.TemperatureSource);
        Assert.True(vm.ShowCpuCurve);
        Assert.False(vm.ShowGpuCurve);
        Assert.Equal(CoolingPadLightingMode.Wave, client.Submitted.LightingMode);
        Assert.Equal(2000, vm.FixedRpm);
        Assert.Contains("1500", vm.ConfiguredTargetText);
    }

    [Fact]
    public async Task UncertainSubmissionPausesUntilSeparateExplicitRefresh()
    {
        var client = new PadClient { FailWrite = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        vm.ApplyStatus(new() { Connected = true, Active = true });
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Writes);
        Assert.False(vm.CanApply);
        Assert.False(vm.CanRelease);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Writes);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.CanApply);
        Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task FailedResultDoesNotAutomaticallyClearServicePauseWithARecoveryRead()
    {
        var client = new PadClient { FailedResult = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        vm.ApplyStatus(new() { Connected = true });
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Writes);
        Assert.Equal(0, client.Reads);
        Assert.False(vm.CanApply);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Reads);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task TransientReadFailurePreservesConfirmedCardAndRecoveryCanRelease()
    {
        var client = new PadClient { FailRead = true };
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        vm.ApplyStatus(new() { Connected = true, ConfirmedTargetRpm = 2100 });
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsVisible);
        Assert.Contains("2100", vm.ConfiguredTargetText);
        vm.ApplyStatus(new() { Connected = true, RecoveryRequired = true });
        Assert.True(vm.CanApply);
        Assert.True(vm.CanRelease);
        client.FailRead = false;
        await vm.ReleaseCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Releases);
    }

    [Fact]
    public async Task InvalidCurveOrRgbDoesNotReachService()
    {
        var client = new PadClient();
        await using var vm = new CoolingPadViewModel(client, new InlineDispatcher());
        vm.ApplyStatus(new() { Connected = true });
        vm.FanMode = vm.FanModes.Single(x => x.Value == CoolingPadFanMode.HostCurve);
        vm.CpuCurve[1].Temperature = vm.CpuCurve[0].Temperature;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, client.Writes);
        vm.CpuCurve[1].Temperature = 65;
        vm.PrimaryColor = "zzzzzz";
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, client.Writes);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken token) { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }

    private sealed class PadClient : OpenBlade.Testing.UnavailableServiceClient, IServiceClient
    {
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public int Releases { get; private set; }
        public bool FailWrite { get; init; }
        public bool FailedResult { get; init; }
        public bool BlockedResult { get; init; }
        public bool FailRead { get; set; }
        public CoolingPadSettings? Submitted { get; private set; }
        public CoolingPadStatusSnapshot NextStatus { get; set; } = new() { Connected = true, Active = true, ConfirmedTargetRpm = 1500 };
        public Task<ControlOperationResult> SetCoolingPadSettingsAsync(CoolingPadSettings settings, CancellationToken token)
        {
            Writes++; Submitted = settings;
            return FailWrite ? Task.FromException<ControlOperationResult>(new IOException("submission uncertain"))
                : Task.FromResult(new ControlOperationResult(BlockedResult ? ControlOperationStatus.Blocked : FailedResult ? ControlOperationStatus.Failed : ControlOperationStatus.Applied, "Applied"));
        }
        public Task<ControlOperationResult> ReleaseCoolingPadControlAsync(CancellationToken token)
        { Releases++; return Task.FromResult(new ControlOperationResult(BlockedResult ? ControlOperationStatus.Blocked : ControlOperationStatus.Applied, "Released")); }
        public Task<ServiceStatusSnapshot> ReadCoolingPadStatusAsync(CancellationToken token)
        {
            Reads++;
            if (FailRead) { throw new IOException("temporarily unavailable"); }
            ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
            return Task.FromResult(new ServiceStatusSnapshot(1, true, false, [], null, [], "Ready", null, null,
                profiles, new DesiredDeviceState(
                    new(profiles.PluggedIn.Performance, SettingValueSource.PowerProfile),
                    new(profiles.PluggedIn.Fans, SettingValueSource.PowerProfile),
                    new(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                    new(profiles.BatteryPolicy, true, false)), PowerSource.PluggedIn, null, null, null, null, false)
            { CoolingPad = NextStatus });
        }
    }
}
