using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class WindowNotificationCoordinatorTests
{
    private static readonly NotificationPreferences AllEnabled = new(
        ProfileAndPowerSourceChangesEnabled: true);
    private static readonly Uri UpdateUri = new("https://example.invalid/update");
    private static readonly WindowNotificationObservation Initial = new(
        PowerSource.PluggedIn, string.Empty, string.Empty);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedOrThrowingUpdateDeliveryRetainsKeysAndCanRetry(bool firmware, bool throws)
    {
        bool succeed = false;
        int deliveries = 0;
        var coordinator = new WindowNotificationCoordinator(_ =>
        {
            deliveries++;
            if (succeed)
            {
                return true;
            }
            if (throws)
            {
                throw new InvalidOperationException("Delivery failed.");
            }
            return false;
        });
        coordinator.RestoreDeliveredUpdates("old-app", "old-firmware");

        Assert.False(DeliverUpdate(coordinator, firmware));
        Assert.Equal("old-app", coordinator.LastNotifiedReleaseTag);
        Assert.Equal("old-firmware", coordinator.LastNotifiedFirmwareSignature);
        succeed = true;
        Assert.True(DeliverUpdate(coordinator, firmware));
        Assert.Equal(firmware ? "old-app" : "v0.21.0", coordinator.LastNotifiedReleaseTag);
        Assert.Equal(firmware ? "firmware-v2" : "old-firmware", coordinator.LastNotifiedFirmwareSignature);
        Assert.False(DeliverUpdate(coordinator, firmware));
        Assert.Equal(2, deliveries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledUpdateDoesNotDeliverOrConsumeKey(bool firmware)
    {
        int deliveries = 0;
        var coordinator = new WindowNotificationCoordinator(_ => { deliveries++; return true; });
        NotificationPreferences disabled = AllEnabled with
        {
            AppUpdatesEnabled = false,
            FirmwareUpdatesEnabled = false,
        };
        Assert.False(DeliverUpdate(coordinator, firmware, disabled));
        Assert.Null(coordinator.LastNotifiedReleaseTag);
        Assert.Null(coordinator.LastNotifiedFirmwareSignature);
        Assert.Equal(0, deliveries);
        Assert.True(DeliverUpdate(coordinator, firmware));
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void UpdateKeysKeepTheirDistinctComparisonRulesAndDestinations()
    {
        var notifications = new List<TrayNotification>();
        var coordinator = new WindowNotificationCoordinator(notification =>
        {
            notifications.Add(notification);
            return true;
        });
        coordinator.RestoreDeliveredUpdates("V0.21.0", "FIRMWARE-V2");
        Assert.False(DeliverUpdate(coordinator, firmware: false));
        Assert.True(DeliverUpdate(coordinator, firmware: true));
        Assert.True(coordinator.OnAppUpdateAvailable(new("v0.22.0", UpdateUri), AllEnabled));
        Assert.Collection(notifications,
            firmware =>
            {
                Assert.Equal("firmware-update", firmware.ReplacementTag);
                Assert.Equal(TrayNotificationDestination.Firmware, firmware.Destination);
            },
            app =>
            {
                Assert.Equal("app-update", app.ReplacementTag);
                Assert.Equal(TrayNotificationDestination.Updates, app.Destination);
                Assert.Equal("OpenBlade 0.22.0 is ready to download.", app.Body);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyFirmwareMigrationPersistsWithoutDeliveryOnlyWhenAllowed(bool includesNewSubsystem)
    {
        int deliveries = 0;
        var coordinator = new WindowNotificationCoordinator(_ => { deliveries++; return true; });
        coordinator.RestoreDeliveredUpdates(null, "legacy");
        var update = new FirmwareUpdateAvailableEventArgs(
            "current", "Firmware available", UpdateUri,
            legacySignature: "legacy", includesThunderboltOrTouchpadUpdate: includesNewSubsystem);
        Assert.False(coordinator.OnFirmwareUpdateAvailable(update, AllEnabled with { FirmwareUpdatesEnabled = false }));
        Assert.Equal("legacy", coordinator.LastNotifiedFirmwareSignature);
        Assert.True(coordinator.OnFirmwareUpdateAvailable(update, AllEnabled));
        Assert.Equal("current", coordinator.LastNotifiedFirmwareSignature);
        Assert.Equal(includesNewSubsystem ? 1 : 0, deliveries);
        Assert.False(coordinator.OnFirmwareUpdateAvailable(update, AllEnabled));
    }

    [Fact]
    public void ColdStartAndPrimingDoNotDeliverExistingState()
    {
        var notifications = new List<TrayNotification>();
        var coordinator = new WindowNotificationCoordinator(notification =>
        {
            notifications.Add(notification);
            return true;
        });
        WindowNotificationObservation current = Initial with
        {
            PowerSource = PowerSource.Battery,
            ControllerAdvisoryText = "Existing controller",
            OperationFailure = "Existing failure",
        };
        ObserveAll(coordinator, current, AllEnabled);
        Assert.False(coordinator.ObservationReady);
        coordinator.Prime(current);
        Assert.True(coordinator.ObservationReady);
        ObserveAll(coordinator, current, AllEnabled);
        Assert.Empty(notifications);
    }

    [Fact]
    public void DisabledCategoriesAdvanceBaselinesAndOnlyNewChangesDeliver()
    {
        var notifications = new List<TrayNotification>();
        var coordinator = new WindowNotificationCoordinator(notification =>
        {
            notifications.Add(notification);
            return true;
        });
        coordinator.Prime(Initial);
        WindowNotificationObservation first = Initial with
        {
            PowerSource = PowerSource.Battery,
            ControllerAdvisoryText = "Controller one",
            OperationFailure = "Failure one",
        };
        ObserveAll(coordinator, first, AllEnabled with
        {
            ProfileAndPowerSourceChangesEnabled = false,
            ControllerAdvisoriesEnabled = false,
            OperationFailuresEnabled = false,
        });
        ObserveAll(coordinator, first, AllEnabled);
        Assert.Empty(notifications);

        WindowNotificationObservation second = first with
        {
            PowerSource = PowerSource.UsbC,
            ControllerAdvisoryText = "Controller two",
            OperationFailure = "Failure two",
        };
        ObserveAll(coordinator, second, AllEnabled);
        ObserveAll(coordinator, second, AllEnabled);
        Assert.Collection(notifications,
            profile =>
            {
                Assert.Equal("profile-power-source", profile.ReplacementTag);
                Assert.Equal(TrayNotificationDestination.Laptop, profile.Destination);
                Assert.Contains("USB-C profile", profile.Body, StringComparison.Ordinal);
            },
            controller =>
            {
                Assert.Equal("controller-advisory", controller.ReplacementTag);
                Assert.Equal(TrayNotificationDestination.Troubleshooting, controller.Destination);
                Assert.Equal("Controller two", controller.Body);
            },
            failure =>
            {
                Assert.Equal("operation-failure", failure.ReplacementTag);
                Assert.Equal(TrayNotificationSeverity.Error, failure.Severity);
                Assert.Equal(TrayNotificationDestination.Troubleshooting, failure.Destination);
                Assert.Equal("Failure two", failure.Body);
            });
    }

    [Fact]
    public void UnknownPowerAndClearedDiagnosticsUpdateStateWithoutDelivery()
    {
        var notifications = new List<TrayNotification>();
        var coordinator = new WindowNotificationCoordinator(notification =>
        {
            notifications.Add(notification);
            return true;
        });
        WindowNotificationObservation active = Initial with
        {
            ControllerAdvisoryText = "Controller",
            OperationFailure = "Failure",
        };
        coordinator.Prime(active);
        ObserveAll(coordinator, Initial with { PowerSource = PowerSource.ExternalUnknown }, AllEnabled);
        Assert.Empty(notifications);
        ObserveAll(coordinator, active, AllEnabled);
        Assert.Equal(3, notifications.Count);
    }

    [Fact]
    public void UnrelatedPropertyNamesDoNotAdvanceObservation()
    {
        int deliveries = 0;
        var coordinator = new WindowNotificationCoordinator(_ => { deliveries++; return true; });
        coordinator.Prime(Initial);
        WindowNotificationObservation battery = Initial with { PowerSource = PowerSource.Battery };
        coordinator.Observe(null, battery, AllEnabled);
        coordinator.Observe("Unrelated", battery, AllEnabled);
        Assert.Equal(0, deliveries);
        coordinator.Observe(nameof(MainViewModel.ActivePowerSource), battery, AllEnabled);
        Assert.Equal(1, deliveries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedObservationDeliveryAdvancesBaselinesWithoutEscaping(bool throws)
    {
        int deliveries = 0;
        var coordinator = new WindowNotificationCoordinator(_ =>
        {
            deliveries++;
            if (throws)
            {
                throw new InvalidOperationException("Delivery failed.");
            }
            return false;
        });
        coordinator.Prime(Initial);
        WindowNotificationObservation changed = Initial with
        {
            PowerSource = PowerSource.Battery,
            ControllerAdvisoryText = "Controller",
            OperationFailure = "Failure",
        };
        ObserveAll(coordinator, changed, AllEnabled);
        Assert.Equal(3, deliveries);
        ObserveAll(coordinator, changed, AllEnabled);
        Assert.Equal(3, deliveries);
    }

    private static bool DeliverUpdate(
        WindowNotificationCoordinator coordinator,
        bool firmware,
        NotificationPreferences? preferences = null) => firmware
        ? coordinator.OnFirmwareUpdateAvailable(new("firmware-v2", "Firmware available", UpdateUri), preferences ?? AllEnabled)
        : coordinator.OnAppUpdateAvailable(new("v0.21.0", UpdateUri), preferences ?? AllEnabled);

    private static void ObserveAll(
        WindowNotificationCoordinator coordinator,
        WindowNotificationObservation observation,
        NotificationPreferences preferences)
    {
        coordinator.Observe(nameof(MainViewModel.ActivePowerSource), observation, preferences);
        coordinator.Observe(nameof(MainViewModel.ControllerAdvisoryText), observation, preferences);
        coordinator.Observe(nameof(MainViewModel.TroubleshootingRecentErrorStatus), observation, preferences);
    }
}
