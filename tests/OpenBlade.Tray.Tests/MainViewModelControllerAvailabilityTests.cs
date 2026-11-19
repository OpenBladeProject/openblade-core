using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task UnverifiedLaptopWithControllers_ShowsUnavailableControlsAndRecovers()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();

        // Issue #950: Synapse stops the initial probe before HID enumeration.
        // A detected model is not yet a verified control interface.
        ServiceStatusSnapshot blocked = CreateStatus(
            verified: false,
            blocked: true,
            conflicts: ["RazerAppEngine", "razerwdl"]);
        await PushAsync(client, dispatcher, blocked);

        Assert.Equal("Controls unavailable", viewModel.ProfileStateLabel);
        Assert.Equal("Controls unavailable", viewModel.ControlAccessStatus);
        Assert.Equal("Controls unavailable", viewModel.TroubleshootingControlsStatus);
        Assert.Equal("Controls unavailable", viewModel.PerformanceSourceLabel);
        Assert.Equal("Controls unavailable", viewModel.FanSourceLabel);
        Assert.Equal("Controls unavailable", viewModel.LightingSourceLabel);
        Assert.Equal("Controls unavailable", viewModel.BatterySourceLabel);
        Assert.True(viewModel.IsReadinessWarning);
        Assert.False(viewModel.IsReadinessError);
        Assert.False(viewModel.IsReadinessReady);
        Assert.False(viewModel.CanControlHardware);
        Assert.False(viewModel.CanRequestHardwareControl);
        Assert.True(viewModel.CanCheckCurrentState);
        Assert.True(viewModel.HasAttention);
        Assert.Equal("Another Razer app is running", viewModel.AttentionSummary);
        Assert.Equal("RazerAppEngine, razerwdl", viewModel.TroubleshootingOtherRazerSoftwareStatus);
        Assert.Contains("Close Synapse", viewModel.HardwareControlUnavailableMessage);
        Assert.Contains("Check current state", viewModel.HardwareControlUnavailableMessage);
        Assert.False(viewModel.HasLightingCapability);
        Assert.True(viewModel.IsLightingControlUnavailableNoticeVisible);
        Assert.Equal(viewModel.HardwareControlUnavailableMessage, viewModel.LightingControlUnavailableMessage);

        // Clearing a controller does not prove that the laptop is present.
        await PushAsync(client, dispatcher, blocked with
        {
            Revision = 2,
            PotentialControllers = [],
        });
        Assert.Equal("Laptop not found", viewModel.ProfileStateLabel);
        Assert.Equal("Laptop not found", viewModel.ControlAccessStatus);
        Assert.False(viewModel.IsReadinessWarning);
        Assert.True(viewModel.IsReadinessError);
        Assert.False(viewModel.CanControlHardware);
        Assert.False(viewModel.HasAttention);
        Assert.DoesNotContain("Close Synapse", viewModel.HardwareControlUnavailableMessage);

        await PushAsync(client, dispatcher,
            CreateStatus(verified: true, blocked: false) with { Revision = 3 });
        Assert.Equal("Ready", viewModel.ProfileStateLabel);
        Assert.Equal("Ready", viewModel.ControlAccessStatus);
        Assert.True(viewModel.IsReadinessReady);
        Assert.False(viewModel.IsReadinessWarning);
        Assert.False(viewModel.IsReadinessError);
        Assert.True(viewModel.CanControlHardware);
        Assert.Empty(viewModel.HardwareControlUnavailableMessage);
    }

    [Fact]
    public async Task UnverifiedLaptopWithDetectionWarning_ShowsUnavailableControlsAndNotifiesWhenCleared()
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        ServiceStatusSnapshot blocked = CreateStatus(verified: false, blocked: true) with
        {
            ControllerDetectionWarning = "Controller inventory failed.",
        };
        await PushAsync(client, dispatcher, blocked);

        Assert.Equal("Controls unavailable", viewModel.ProfileStateLabel);
        Assert.Equal("Controls unavailable", viewModel.ControlAccessStatus);
        Assert.Equal("Controls unavailable", viewModel.PerformanceSourceLabel);
        Assert.True(viewModel.IsReadinessWarning);
        Assert.False(viewModel.IsReadinessError);
        Assert.False(viewModel.CanControlHardware);
        Assert.Equal("Controller detection needs attention", viewModel.AttentionSummary);
        Assert.Contains("Check current state", viewModel.HardwareControlUnavailableMessage);
        Assert.Equal(viewModel.HardwareControlUnavailableMessage, viewModel.LightingControlUnavailableMessage);

        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        await PushAsync(client, dispatcher, blocked with
        {
            Revision = 2,
            ControllerDetectionWarning = null,
        });
        Assert.Equal("Laptop not found", viewModel.ProfileStateLabel);
        Assert.True(viewModel.IsReadinessError);
        Assert.False(viewModel.IsReadinessWarning);
        Assert.DoesNotContain("Close Synapse", viewModel.HardwareControlUnavailableMessage);
        Assert.DoesNotContain("Close Synapse", viewModel.LightingControlUnavailableMessage);
        Assert.Contains(nameof(MainViewModel.ProfileStateLabel), changed);
        Assert.Contains(nameof(MainViewModel.ControlAccessStatus), changed);
        Assert.Contains(nameof(MainViewModel.HardwareControlUnavailableMessage), changed);
        Assert.Contains(nameof(MainViewModel.LightingControlUnavailableMessage), changed);
        Assert.Contains(nameof(MainViewModel.IsReadinessWarning), changed);
        Assert.Contains(nameof(MainViewModel.IsReadinessError), changed);
    }

    [Theory]
    [InlineData(SessionControlAccess.InactiveSession, "Another user is controlling OpenBlade", "active Windows user session")]
    [InlineData(SessionControlAccess.NotInteractive, "Interactive session required", "interactive Windows session")]
    public async Task UnverifiedLaptopWithControllers_PreservesSessionSpecificGuidance(
        SessionControlAccess access,
        string expectedStatus,
        string expectedGuidance)
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        await PushAsync(client, dispatcher,
            CreateStatus(verified: false, blocked: true, conflicts: ["RazerAppEngine"]) with
            {
                ControlAccess = access,
            });

        Assert.Equal("Controls unavailable", viewModel.ProfileStateLabel);
        Assert.Equal(expectedStatus, viewModel.ControlAccessStatus);
        Assert.Contains(expectedGuidance, viewModel.HardwareControlUnavailableMessage);
        Assert.DoesNotContain("Close Synapse", viewModel.HardwareControlUnavailableMessage);
        Assert.False(viewModel.CanRequestHardwareControl);
        Assert.False(viewModel.CanControlHardware);
    }

    [Theory]
    [InlineData(true, FirmwareCompatibilityStatus.ModelScoped, "Laptop not supported", false)]
    [InlineData(false, FirmwareCompatibilityStatus.UntestedBlocked, "Incompatible firmware", true)]
    [InlineData(false, FirmwareCompatibilityStatus.ModelScoped, "Checking laptop", false)]
    public async Task UnverifiedLaptopWithControllers_PreservesSpecificAdmissionStates(
        bool unsupported,
        FirmwareCompatibilityStatus firmware,
        string expected,
        bool warning)
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();

        await PushAsync(client, dispatcher,
            CreateStatus(verified: false, blocked: true, conflicts: ["RazerAppEngine"]) with
            {
                UnsupportedLaptop = unsupported,
                FirmwareCompatibility = firmware,
                ObservationReason = expected == "Checking laptop"
                    ? DeviceObservationReason.CheckingConnection
                    : DeviceObservationReason.None,
            });

        Assert.Equal(expected, viewModel.ProfileStateLabel);
        Assert.Equal(expected, viewModel.ControlAccessStatus);
        Assert.Equal(expected, viewModel.PerformanceSourceLabel);
        Assert.Equal(warning, viewModel.IsReadinessWarning);
        Assert.False(viewModel.CanControlHardware);
    }
}
