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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ExactHostWithAbsentDriverFilesReportsDriverMissing(
        bool commonMissing,
        bool deviceMissing)
    {
        RazerSnapTapDriverSnapshot exact = ExactSnapshot();
        RazerSnapTapDriverSnapshot snapshot = exact with
        {
            RzCommonSha256 = commonMissing ? null : exact.RzCommonSha256,
            RzDeviceSha256 = deviceMissing ? null : exact.RzDeviceSha256,
            KeyboardDevices = [exact.KeyboardDevices[0] with { UpperFilters = [] }],
        };
        var admission = new RazerSnapTapDriverAdmission(() => snapshot);

        Assert.False(admission.TryGetEndpoint(out _, out bool driverMissing));
        Assert.True(driverMissing);
        Assert.False(admission.TryGetEndpoint(out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrongRemainingDriverHashDoesNotReportDriverMissing(bool commonMissing)
    {
        RazerSnapTapDriverSnapshot exact = ExactSnapshot();
        RazerSnapTapDriverSnapshot snapshot = exact with
        {
            RzCommonSha256 = commonMissing ? null : new string('0', 64),
            RzDeviceSha256 = commonMissing ? new string('0', 64) : null,
        };
        var admission = new RazerSnapTapDriverAdmission(() => snapshot);

        Assert.False(admission.TryGetEndpoint(out _, out bool driverMissing));
        Assert.False(driverMissing);
    }

    [Fact]
    public void MissingDriverRequiresTrustedHostControllerAndKeyboardObservation()
    {
        RazerSnapTapDriverSnapshot exact = ExactSnapshot();
        RazerSnapTapDriverSnapshot missing = exact with
        {
            RzCommonSha256 = null,
            RzDeviceSha256 = null,
            KeyboardDevices = [exact.KeyboardDevices[0] with { UpperFilters = [] }],
        };
        RazerSnapTapKeyboardDevice keyboard = missing.KeyboardDevices[0];
        RazerSnapTapDriverSnapshot[] untrusted =
        [
            missing with { BiosProductName = "Blade 16 - RZ09-0528" },
            missing with { ActiveConsoleSession = false },
            missing with { CompetingControllerDetected = true },
            missing with { ControllerObservationComplete = false },
            missing with { KeyboardDevices = [] },
            missing with { KeyboardDevices = [keyboard, keyboard] },
            missing with { KeyboardDevices = [keyboard with { ContainerId = Guid.Empty }] },
            missing with { KeyboardDevices = [keyboard with { Ancestors = [] }] },
        ];

        foreach (RazerSnapTapDriverSnapshot snapshot in untrusted)
        {
            var admission = new RazerSnapTapDriverAdmission(() => snapshot);
            Assert.False(admission.TryGetEndpoint(out _, out bool driverMissing));
            Assert.False(driverMissing);
        }
    }

    [Fact]
    public void ObservationFailuresDoNotReportDriverMissing()
    {
        Func<RazerSnapTapDriverSnapshot>[] captures =
        [
            () => throw new IOException("Native discovery failed."),
            () => throw new UnauthorizedAccessException("Driver hash could not be read."),
        ];

        foreach (Func<RazerSnapTapDriverSnapshot> capture in captures)
        {
            var admission = new RazerSnapTapDriverAdmission(capture);
            Assert.False(admission.TryGetEndpoint(out _, out bool driverMissing));
            Assert.False(driverMissing);
        }
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

    [Fact]
    public void EveryCurrentSupportedModelHasAnExactReviewedDriverProfile()
    {
        var models = OpenBlade.Contracts.RazerBladeModelCatalog.All;
        Assert.Equal(models.Count, RazerSnapTapDriverProfiles.All.Count);
        foreach (var model in models)
        {
            var profile = Assert.Single(RazerSnapTapDriverProfiles.All,
                p => p.ProductId == model.UsbProductId);
            Assert.EndsWith(model.ModelNumber, profile.ProductName, StringComparison.Ordinal);
            Assert.Equal(model.ModelNumber == "RZ09-0581", profile.PhysicallyValidated);
            Assert.Equal(model.ModelNumber == "RZ09-0581", profile.AutomaticInstallationAvailable);
            Assert.Equal(64, profile.DeviceSha256.Length);
        }
    }

    public static IEnumerable<object[]> ReviewedProfiles() =>
        RazerSnapTapDriverProfiles.All.Select(profile => new object[] { profile.ProductId });

    [Theory]
    [MemberData(nameof(ReviewedProfiles))]
    public void ReviewedModelsUseOneBackendWithExactModelAdmission(string productId)
    {
        RazerSnapTapDriverSnapshot snapshot = ModelSnapshot(productId);
        Assert.True(RazerSnapTapDriverAdmission.TryAdmit(snapshot, out var endpoint));
        Assert.Equal(productId, endpoint.Profile.ProductId);
        Assert.Equal(snapshot.ControlDevices[0].DevicePath, endpoint.Path);

        foreach (RazerSnapTapDriverProfile other in RazerSnapTapDriverProfiles.All.Where(p => p.ProductId != productId))
        {
            RazerSnapTapDriverSnapshot foreign = ModelSnapshot(other.ProductId);
            Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot with { RzDeviceSha256 = foreign.RzDeviceSha256 }, out _));
            Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot with { ControlDevices = foreign.ControlDevices }, out _));
            Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot with { KeyboardDevices = foreign.KeyboardDevices }, out _));
            Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot with { BiosProductName = foreign.BiosProductName }, out _));
        }
    }

    [Theory]
    [MemberData(nameof(ReviewedProfiles))]
    public void EveryModelRetainsFreshSessionControllerHashAndTopologyGuards(string productId)
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot(productId);
        RazerSnapTapKeyboardDevice keyboard = good.KeyboardDevices[0];
        RazerSnapTapControlDevice control = good.ControlDevices[0];
        RazerSnapTapDriverSnapshot[] rejected =
        [
            good with { BiosManufacturer = "Other" },
            good with { SystemManufacturer = "Other" },
            good with { SystemProductName = good.SystemProductName + " suffix" },
            good with { ActiveConsoleSession = false },
            good with { ControllerObservationComplete = false },
            good with { CompetingControllerDetected = true },
            good with { RzCommonSha256 = new string('0', 64) },
            good with { RzDeviceSha256 = new string('0', 64) },
            good with { ControlDevices = [] },
            good with { ControlDevices = [control, control] },
            good with { ControlDevices = [control with { Ancestors = [] }] },
            good with { KeyboardDevices = [] },
            good with { KeyboardDevices = [keyboard, keyboard] },
            good with { KeyboardDevices = [keyboard with { ContainerId = Guid.NewGuid() }] },
            good with { KeyboardDevices = [keyboard with { UpperFilters = ["Other"] }] },
            good with { KeyboardDevices = [keyboard with { UpperFilters = [keyboard.UpperFilters[0], "Other"] }] },
            good with { KeyboardDevices = [keyboard with { Ancestors = [] }] },
            good with { KeyboardDevices = [keyboard with { InstanceId = keyboard.InstanceId.Replace("COL01", "COL02") }] },
        ];
        RazerSnapTapDriverSnapshot current = good;
        var admission = new RazerSnapTapDriverAdmission(() => current);
        Assert.True(admission.TryGetEndpoint(out var endpoint));
        foreach (var invalid in rejected)
        {
            current = invalid;
            Assert.False(RazerSnapTapDriverAdmission.TryAdmit(current, out _));
            Assert.False(admission.IsCurrent(endpoint));
        }
    }

    [Theory]
    [MemberData(nameof(ReviewedProfiles))]
    public void MissingDriverNeverOffersAnotherModelsInstaller(string productId)
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot(productId);
        var admission = new RazerSnapTapDriverAdmission(() => good with
        {
            RzDeviceSha256 = null,
            KeyboardDevices = [good.KeyboardDevices[0] with { UpperFilters = [] }],
        });
        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
        Assert.Equal(productId == "02E0" ? RazerSnapTapAdmissionFailure.DriverMissing
            : RazerSnapTapAdmissionFailure.ManualDriverSetupRequired, failure);
    }

    [Fact]
    public void ModelProfilesPinTheReviewedVendorFiltersAndKeepValidationProvenanceSeparate()
    {
        RazerSnapTapDriverProfile blade16 = Assert.Single(RazerSnapTapDriverProfiles.All, p => p.ProductId == "02C6");
        Assert.Equal("Blade 16 - RZ09-0528", blade16.ProductName);
        Assert.Equal("307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82", blade16.DeviceSha256);
        Assert.Equal("RzDev_02c6.sys", blade16.DeviceFileName);
        Assert.False(blade16.AutomaticInstallationAvailable);
        Assert.False(blade16.PhysicallyValidated);
        Assert.True(Assert.Single(RazerSnapTapDriverProfiles.All, p => p.ProductId == "02E0").PhysicallyValidated);
    }

    private static RazerSnapTapDriverSnapshot ModelSnapshot(string productId)
    {
        var profile = Assert.Single(RazerSnapTapDriverProfiles.All, p => p.ProductId == productId);
        var exact = ExactSnapshot();
        return exact with
        {
            BiosProductName = profile.ProductName,
            SystemProductName = profile.ProductName,
            RzDeviceSha256 = profile.DeviceSha256,
            ControlDevices = [exact.ControlDevices[0] with
            {
                DevicePath = exact.ControlDevices[0].DevicePath.Replace("02e0", productId.ToLowerInvariant()),
                InstanceId = exact.ControlDevices[0].InstanceId.Replace("02E0", productId),
                Ancestors = exact.ControlDevices[0].Ancestors.Select(a => a.Replace("02E0", productId)).ToArray(),
            }],
            KeyboardDevices = [exact.KeyboardDevices[0] with
            {
                InstanceId = exact.KeyboardDevices[0].InstanceId.Replace("02E0", productId),
                UpperFilters = [$"RzDev_{productId.ToLowerInvariant()}"],
                Ancestors = exact.KeyboardDevices[0].Ancestors.Select(a => a.Replace("02E0", productId)).ToArray(),
            }],
        };
    }
    private static RazerSnapTapDriverSnapshot ExactSnapshot() => new(
        "Razer",
        "Blade 16 - RZ09-0581",
        "Razer",
        "Blade 16 - RZ09-0581",
        RazerSnapTapDriverAdmission.RzCommonSha256,
        "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787",
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
