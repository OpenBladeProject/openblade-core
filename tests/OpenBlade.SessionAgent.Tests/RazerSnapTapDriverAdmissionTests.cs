namespace OpenBlade.SessionAgent.Tests;

public sealed class RazerSnapTapDriverAdmissionTests
{
    private static readonly Guid LaptopContainer = new("D937EC4C-C08F-4CEB-A72D-E6193129A748");

    [Fact]
    public void ExactDriverKeyboardAndControlTupleIsAdmitted()
    {
        RazerSnapTapDriverSnapshot snapshot = ExactSnapshot();

        Assert.True(RazerSnapTapDriverAdmission.TryAdmit(snapshot, out RazerSnapTapDriverEndpoint endpoint));
        Assert.NotNull(endpoint);
        Assert.Equal(snapshot.ControlDevices[0].DevicePath, endpoint.Path);
        Assert.Equal(64, endpoint.IdentityToken.Length);
    }

    [Theory]
    [InlineData("bios-manufacturer")]
    [InlineData("bios-product")]
    [InlineData("system-manufacturer")]
    [InlineData("system-product")]
    public void BothRegistryHostIdentitiesMustMatchExactly(string field)
    {
        RazerSnapTapDriverSnapshot snapshot = ExactSnapshot();
        snapshot = field switch
        {
            "bios-manufacturer" => snapshot with { BiosManufacturer = "Other" },
            "bios-product" => snapshot with { BiosProductName = "Blade 16 - RZ09-0528" },
            "system-manufacturer" => snapshot with { SystemManufacturer = "Other" },
            _ => snapshot with { SystemProductName = "Blade 16 - RZ09-05819EN4" },
        };

        Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothPinnedDriverHashesAreRequired(bool commonDriver)
    {
        RazerSnapTapDriverSnapshot snapshot = ExactSnapshot();
        snapshot = commonDriver
            ? snapshot with { RzCommonSha256 = new string('0', 64) }
            : snapshot with { RzDeviceSha256 = new string('0', 64) };

        Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot, out _));
    }

    [Fact]
    public void ExternalKeyboardWithDifferentContainerCannotSupplyLaptopKeyboardIdentity()
    {
        RazerSnapTapKeyboardDevice external = ExactSnapshot().KeyboardDevices[0] with
        {
            ContainerId = Guid.NewGuid(),
        };
        RazerSnapTapDriverSnapshot snapshot = ExactSnapshot() with { KeyboardDevices = [external] };

        Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot, out _));
    }

    [Fact]
    public void WrongKeyboardCollectionFilterOrAncestorIsRejected()
    {
        RazerSnapTapDriverSnapshot exact = ExactSnapshot();
        RazerSnapTapKeyboardDevice keyboard = exact.KeyboardDevices[0];
        Assert.False(RazerSnapTapDriverAdmission.TryAdmit(exact with
        {
            KeyboardDevices = [keyboard with { InstanceId = keyboard.InstanceId.Replace("COL01", "COL02") }],
        }, out _));
        Assert.False(RazerSnapTapDriverAdmission.TryAdmit(exact with
        {
            KeyboardDevices = [keyboard with { UpperFilters = ["kbdclass"] }],
        }, out _));
        Assert.False(RazerSnapTapDriverAdmission.TryAdmit(exact with
        {
            KeyboardDevices = [keyboard with { Ancestors = [@"USB\VID_1532&PID_02E0&MI_02\control"] }],
        }, out _));
    }

    [Fact]
    public void WrongControlIdentityOrAmbiguousControlIsRejected()
    {
        RazerSnapTapDriverSnapshot exact = ExactSnapshot();
        RazerSnapTapControlDevice control = exact.ControlDevices[0];
        Assert.False(RazerSnapTapDriverAdmission.TryAdmit(exact with
        {
            ControlDevices = [control with { ServiceName = "RzCommonOther" }],
        }, out _));
        Assert.False(RazerSnapTapDriverAdmission.TryAdmit(exact with
        {
            ControlDevices = [control, control with { DevicePath = control.DevicePath + "duplicate" }],
        }, out _));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CompetingOrIncompleteControllerObservationFailsClosed(bool detected, bool complete)
    {
        RazerSnapTapDriverSnapshot snapshot = ExactSnapshot() with
        {
            CompetingControllerDetected = detected,
            ControllerObservationComplete = complete,
        };

        Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot, out _));
    }

    [Fact]
    public void IsCurrentRepeatsSnapshotAndRejectsAnyIdentityChange()
    {
        RazerSnapTapDriverSnapshot current = ExactSnapshot();
        var admission = new RazerSnapTapDriverAdmission(() => current);
        Assert.True(admission.TryGetEndpoint(out RazerSnapTapDriverEndpoint endpoint));

        Assert.True(admission.IsCurrent(endpoint));
        current = current with { RzDeviceSha256 = new string('0', 64) };
        Assert.False(admission.IsCurrent(endpoint));
    }

    [Theory]
    [InlineData(1u, 1u, true)]
    [InlineData(2u, 1u, false)]
    [InlineData(0u, 0u, false)]
    [InlineData(1u, uint.MaxValue, false)]
    [InlineData(uint.MaxValue, uint.MaxValue, false)]
    public void OnlyTheActivePhysicalConsoleSessionCanOwnTheDriver(uint current, uint active, bool admitted)
    {
        Assert.Equal(admitted, WindowsRazerSnapTapDriverDiscovery.IsActiveConsoleSession(current, active));
    }

    [Fact]
    public void LosingConsoleOwnershipInvalidatesAnAdmittedEndpoint()
    {
        RazerSnapTapDriverSnapshot current = ExactSnapshot();
        var admission = new RazerSnapTapDriverAdmission(() => current);
        Assert.True(admission.TryGetEndpoint(out var endpoint));
        current = current with { ActiveConsoleSession = false };
        Assert.False(admission.IsCurrent(endpoint));
        Assert.False(admission.TryGetEndpoint(out _));
    }

    private static RazerSnapTapDriverSnapshot ExactSnapshot() => new(
        "Razer",
        "Blade 16 - RZ09-0581",
        "Razer",
        "Blade 16 - RZ09-0581",
        RazerSnapTapDriverAdmission.RzCommonSha256,
        RazerSnapTapDriverAdmission.RzDeviceSha256,
        [new RazerSnapTapControlDevice(
            @"\\?\rzcontrol#vid_1532&pid_02e0&mi_00#laptop#{e3be005d-d130-4910-88ff-09ae02f680e9}",
            @"RZCONTROL\VID_1532&PID_02E0&MI_00\laptop",
            "RzCommon",
            LaptopContainer,
            [@"RZCONTROL\VID_1532&PID_02E0&MI_00\laptop", @"USB\VID_1532&PID_02E0&MI_02\laptop"])],
        [new RazerSnapTapKeyboardDevice(
            @"HID\VID_1532&PID_02E0&MI_01&COL01\laptop",
            ["RzDev_02e0"],
            LaptopContainer,
            [@"HID\VID_1532&PID_02E0&MI_01&COL01\laptop", @"USB\VID_1532&PID_02E0&MI_01\laptop"])],
        CompetingControllerDetected: false,
        ControllerObservationComplete: true,
        ActiveConsoleSession: true);
}
