using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class BrightnessHidDriverManagerTests
{
    private static readonly Guid HidClass = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");

    [Theory]
    [InlineData(@"{745a17a0-74d3-11d0-b6fe-00a0c90f57da}\0000")]
    [InlineData(@"{745A17A0-74D3-11D0-B6FE-00A0C90F57DA}\9999")]
    public void TryValidateDriverKey_AcceptsExactClassKey(string value)
    {
        Assert.True(WindowsBrightnessHidDriverManager.TryValidateDriverKey(value, HidClass));
    }

    [Theory]
    [InlineData(@"0000")]
    [InlineData(@"{745a17a0-74d3-11d0-b6fe-00a0c90f57da}\000")]
    [InlineData(@"{745a17a0-74d3-11d0-b6fe-00a0c90f57da}\00000")]
    [InlineData(@"{745a17a0-74d3-11d0-b6fe-00a0c90f57da}\00af")]
    [InlineData(@"{4d36e96f-e325-11ce-bfc1-08002be10318}\0000")]
    [InlineData(@"{745a17a0-74d3-11d0-b6fe-00a0c90f57da}\0000\extra")]
    public void TryValidateDriverKey_RejectsAnythingOutsideExactClassKey(string value)
    {
        Assert.False(WindowsBrightnessHidDriverManager.TryValidateDriverKey(value, HidClass));
    }

    [Fact]
    public void InstallState_RequiresCompleteIdentityForExistingDevice()
    {
        Assert.False(new BrightnessHidDriverInstallState(true).IsWellFormed);
        Assert.True(new BrightnessHidDriverInstallState(false).IsWellFormed);
        Assert.True(new BrightnessHidDriverInstallState(
            true,
            @"ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID\0000",
            @"C:\Windows\INF\oem42.inf",
            new Version(1, 0, 0, 0),
            WindowsBrightnessHidDriverManager.DriverProvider,
            true).IsWellFormed);
    }

    [Fact]
    public void InstallState_AllowsOwnedUnboundRootDeviceForRepair()
    {
        BrightnessHidDriverInstallState unbound = new(
            true,
            @"ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID\0000");

        Assert.True(unbound.IsWellFormed);
        Assert.False(unbound.IsBound);
        Assert.True(WindowsBrightnessHidDriverManager.IsServiceablePriorState(unbound));
    }

    [Fact]
    public void IsServiceablePriorState_FailsClosedForStoppedOwnedDevice()
    {
        BrightnessHidDriverInstallState stopped = new(
            true,
            @"ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID\0000",
            @"C:\Windows\INF\oem42.inf",
            new Version(1, 0, 0, 0),
            WindowsBrightnessHidDriverManager.DriverProvider,
            false);

        Assert.True(stopped.IsWellFormed);
        Assert.False(WindowsBrightnessHidDriverManager.IsServiceablePriorState(stopped));
        Assert.True(WindowsBrightnessHidDriverManager.IsServiceablePriorState(new(false)));
    }

    [Fact]
    public void IsOwnedPriorState_RejectsForeignDriverProvider()
    {
        BrightnessHidDriverInstallState foreign = new(
            true,
            WindowsBrightnessHidDriverManager.DeviceInstanceId,
            @"C:\Windows\INF\oem42.inf",
            new Version(1, 0, 0, 0),
            "Foreign Provider",
            true);

        Assert.False(WindowsBrightnessHidDriverManager.IsOwnedPriorState(foreign));
        Assert.True(WindowsBrightnessHidDriverManager.IsOwnedPriorState(new(false)));
        Assert.True(WindowsBrightnessHidDriverManager.IsOwnedPriorState(new(
            true,
            WindowsBrightnessHidDriverManager.DeviceInstanceId)));
    }

    [Fact]
    public void DeviceInstanceContract_RejectsAdditionalRootInstances()
    {
        Assert.Equal(
            @"ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID\0000",
            WindowsBrightnessHidDriverManager.DeviceInstanceId);
        Assert.False(string.Equals(
            @"ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID\0001",
            WindowsBrightnessHidDriverManager.DeviceInstanceId,
            StringComparison.OrdinalIgnoreCase));
        Assert.True(WindowsBrightnessHidDriverManager.IsExpectedDeviceInstanceId(
            @"root\ossblade_openblade_brightness_hid\0000"));
        Assert.False(WindowsBrightnessHidDriverManager.IsExpectedDeviceInstanceId(
            @"ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID\0001"));
    }

    [Fact]
    public void RestoreValidation_RequiresExactPublishedPackageAndStartedState()
    {
        BrightnessHidDriverInstallState prior = new(
            true,
            WindowsBrightnessHidDriverManager.DeviceInstanceId,
            @"C:\Windows\INF\oem42.inf",
            new Version(1, 0, 0, 0),
            WindowsBrightnessHidDriverManager.DriverProvider,
            true);

        Assert.True(WindowsBrightnessHidDriverManager.IsExactRestoredState(prior, prior));
        Assert.False(WindowsBrightnessHidDriverManager.IsExactRestoredState(
            prior with { PublishedInfPath = @"C:\Windows\INF\oem43.inf" },
            prior));
        Assert.False(WindowsBrightnessHidDriverManager.IsExactRestoredState(
            prior with { DeviceStarted = false },
            prior));
    }
}
