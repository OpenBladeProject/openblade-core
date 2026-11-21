using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    private const string WindowsDynamicLightingKeyboardAdvisory =
        "Windows Dynamic Lighting is on and can override OpenBlade keyboard lighting. If your OpenBlade lighting doesn’t show, turn off Use Dynamic Lighting on my devices in Settings > Personalization > Dynamic Lighting.";
    private const string WindowsDynamicLightingVaporChamberAdvisory =
        "Windows Dynamic Lighting is on and can override OpenBlade keyboard and Vapor chamber lighting. If your OpenBlade lighting doesn’t show, turn off Use Dynamic Lighting on my devices in Settings > Personalization > Dynamic Lighting.";

    [Theory]
    [InlineData("RZ09-0528", false)]
    [InlineData("RZ09-0529", true)]
    [InlineData("RZ09-0509", false)]
    [InlineData("RZ09-0510", false)]
    [InlineData("RZ09-0530", false)]
    public async Task WindowsDynamicLightingAdvisory_ShowsANoticeWithoutDisablingLighting(
        string modelNumber,
        bool coversVaporChamber)
    {
        using var culture = new CultureScope("en-US");
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = AdvisoryStatus(modelNumber);
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanControlLighting);
        Assert.False(viewModel.IsWindowsDynamicLightingAdvisoryVisible);
        var notified = new ConcurrentQueue<string>();
        viewModel.PropertyChanged += (_, args) => notified.Enqueue(args.PropertyName ?? string.Empty);

        client.Publish(status with
        {
            Revision = 2,
            WindowsDynamicLightingAdvisory = WindowsDynamicLightingAdvisory.MayControl,
        });
        await WaitUntilAsync(() => viewModel.IsWindowsDynamicLightingAdvisoryVisible);

        Assert.Contains(nameof(MainViewModel.IsWindowsDynamicLightingAdvisoryVisible), notified);
        Assert.Contains(nameof(MainViewModel.WindowsDynamicLightingAdvisoryText), notified);
        Assert.Equal(
            coversVaporChamber
                ? WindowsDynamicLightingVaporChamberAdvisory
                : WindowsDynamicLightingKeyboardAdvisory,
            viewModel.WindowsDynamicLightingAdvisoryText);
        // Advisory only: OpenBlade lighting stays fully available.
        Assert.True(viewModel.CanControlLighting);
        Assert.True(viewModel.CanSetLightingRefreshRate);
        Assert.False(viewModel.IsLightingControlUnavailable);
        Assert.False(viewModel.UseWindowsDynamicLighting);

        client.Publish(status with
        {
            Revision = 3,
            WindowsDynamicLightingAdvisory = WindowsDynamicLightingAdvisory.Off,
        });
        await WaitUntilAsync(() => !viewModel.IsWindowsDynamicLightingAdvisoryVisible);
        Assert.Equal(string.Empty, viewModel.WindowsDynamicLightingAdvisoryText);
        Assert.True(viewModel.CanControlLighting);
    }

    [Fact]
    public async Task WindowsDynamicLightingAdvisory_HidesOutsideTheActiveSession()
    {
        using var culture = new CultureScope("en-US");
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = AdvisoryStatus("RZ09-0529") with
        {
            WindowsDynamicLightingAdvisory = WindowsDynamicLightingAdvisory.MayControl,
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.IsWindowsDynamicLightingAdvisoryVisible);

        // Only the control access changes; the advisory field does not.
        client.Publish(status with
        {
            Revision = 2,
            ControlAccess = SessionControlAccess.InactiveSession,
        });

        await WaitUntilAsync(() => !viewModel.IsWindowsDynamicLightingAdvisoryVisible);
    }

    [Fact]
    public async Task WindowsDynamicLightingAdvisory_StaysHiddenAfterTheUserDelegatedLighting()
    {
        using var culture = new CultureScope("en-US");
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        client.Publish(AdvisoryStatus(RazerBladeModelCatalog.Rz090528ModelNumber) with
        {
            KeyboardLightingControlMode = KeyboardLightingControlMode.WindowsDynamicLighting,
            WindowsDynamicLightingAdvisory = WindowsDynamicLightingAdvisory.MayControl,
        });
        await WaitUntilAsync(() => viewModel.UseWindowsDynamicLighting);

        Assert.False(viewModel.IsWindowsDynamicLightingAdvisoryVisible);
    }

    [Fact]
    public async Task WindowsDynamicLightingToggleVisibilityNotifiesWhenTheEditorSwitches()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            SupportsIndependentLightingControls = true,
            SupportsKeyboardBrightness = true,
            SupportsVaporChamberLighting = true,
            SupportsTouchpadStatusIndicator = true,
            KeyboardLightingLayoutId = KeyboardLightingLayoutIds.Rz09_0529,
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off, LightingEffect.Static, LightingEffect.Sequence,
            },
        };
        var fields = new IndependentLightingSettings(Brightness: 66,
            VaporChamberLightingEnabled: true, TouchpadStatusIndicatorEnabled: false);
        LightingSettings baseline = LightingSettingsFactory.Static(new(10, 20, 30), 66) with
        {
            IndependentControls = fields,
            ControlsKeyboardEffect = true,
        };
        ServiceStatusSnapshot status = CreateStatus(true, false, capabilities: capabilities);
        status = status with
        {
            Profiles = status.Profiles with
            {
                PluggedIn = status.Profiles.PluggedIn with { Lighting = baseline },
            },
            DesiredState = status.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(baseline, SettingValueSource.PowerProfile),
            },
            ConfirmedState = status.ConfirmedState! with { IndependentLighting = fields },
            Firmware = new FirmwareVersions("1.00", "1.00", "1.0.0.0", "Unavailable", "RZ09-0529"),
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanControlLighting && viewModel.UsesIndependentLightingControls);
        Assert.False(viewModel.IsWindowsDynamicLightingAvailable);
        var notified = new ConcurrentQueue<string>();
        viewModel.PropertyChanged += (_, args) => notified.Enqueue(args.PropertyName ?? string.Empty);

        Assert.True(viewModel.TryImportKeyboardLighting(new(
            "Imported colors", "User", null, 1,
            LightingSettingsFactory.Static(new(40, 50, 60), 66))));

        Assert.False(viewModel.UsesIndependentLightingControls);
        Assert.True(viewModel.IsWindowsDynamicLightingAvailable);
        Assert.Contains(nameof(MainViewModel.IsWindowsDynamicLightingAvailable), notified);
    }

    private static ServiceStatusSnapshot AdvisoryStatus(string modelNumber) =>
        CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                SupportedLightingRefreshRates = new HashSet<LightingRefreshRate>
                {
                    LightingRefreshRate.ThirtyHz,
                    LightingRefreshRate.SixtyHz,
                },
            }) with
        {
            Firmware = new FirmwareVersions(
                "1.00",
                "1.00",
                "1.0.0.0",
                "Unavailable",
                modelNumber),
        };
}
