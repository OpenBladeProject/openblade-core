using System.ComponentModel;
using OpenBlade.Contracts;
using OpenBlade.Platform;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class RazerSnapTapDriverAdmissionTests
{
    private static readonly Guid LaptopContainer = new("D937EC4C-C08F-4CEB-A72D-E6193129A748");

    [Fact]
    public void InvalidDataDuringCaptureReturnsDiscoveryFailureWithoutEscaping()
    {
        var admission = new RazerSnapTapDriverAdmission(() =>
            throw new InvalidDataException("The endpoint has no installed driver package."));
        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));
        Assert.Equal(RazerSnapTapAdmissionFailure.DiscoveryFailed, failure);
        Assert.Equal(nameof(InvalidDataException), detail?.ExceptionType);
        Assert.Equal("The endpoint has no installed driver package.", detail?.Message);
    }

    [Theory]
    [MemberData(nameof(ReviewedProfiles))]
    public void BothPackageFailuresSurviveAdmissionForEveryReviewedProfile(string productId)
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot(productId);
        string service = RazerSnapTapDriverProfiles.All.Single(profile => profile.ProductId == productId).DeviceService;
        var common = DiagnosticFailure.FromException("DriverPackage", "RzCommon", "EndpointBinding",
            "EndpointDriverMissing", new InvalidDataException("The endpoint has no installed driver package."));
        var device = DiagnosticFailure.FromException("DriverPackage", service, "InfContract",
            "HardwareServiceMismatch", new InvalidDataException("Exact service declaration absent."));
        var admission = new RazerSnapTapDriverAdmission(() => good with
        {
            CommonDriver = new(true, false, null, "legacy common") { FailureDetail = common },
            DeviceDriver = new(true, false, null, "legacy device") { FailureDetail = device },
        });

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted, failure);
        Assert.Equal("RzCommon", detail?.Cause?.Operation);
        Assert.Equal("EndpointDriverMissing", detail?.Cause?.Reason);
        Assert.Equal(service, detail?.Cause?.Related?[0].Operation);
        Assert.Equal("Exact service declaration absent.", detail?.Cause?.Related?[0].Message);
        Assert.True(detail?.IsValid());
        Assert.False(admission.TryGetEndpoint(out _, out bool missing));
        Assert.False(missing);
    }

    [Fact]
    public void ControllerEnumerationFailureRetainsBoundedExceptionWithoutProcessIdentity()
    {
        var result = WindowsRazerSnapTapDriverDiscovery.ObserveControllers(
            () => throw new Win32Exception(
                5,
                @"Process inventory failed under C:\Users\private-owner\diagnostics."));

        Assert.False(result.Detected);
        Assert.False(result.Complete);
        Assert.Equal("ObserveControllers", result.Failure?.Operation);
        Assert.Equal("EnumerateProcesses", result.Failure?.Stage);
        Assert.Equal("ProcessEnumerationFailed", result.Failure?.Reason);
        Assert.Equal(5, result.Failure?.NativeErrorCode);
        Assert.DoesNotContain("private-owner", result.Failure?.Message, StringComparison.Ordinal);
        Assert.True(result.Failure?.IsValid());
    }

    [Fact]
    public void IncompleteControllerObservationRetainsFailureAlongsidePackageEvidence()
    {
        RazerSnapTapDriverSnapshot good = ExactSnapshot();
        DiagnosticFailure observation = DiagnosticFailure.FromException(
            "SnapTapAdmission",
            "ObserveControllers",
            "ReadProcessName",
            "ProcessInspectionFailed",
            new Win32Exception(5, @"Process inspection failed under C:\Users\private-owner\state."));
        DiagnosticFailure package = DiagnosticFailure.FromException(
            "DriverPackage",
            "RzCommon",
            "CatalogTrust",
            "CatalogTrustFailed",
            new InvalidDataException("The installed package is not trusted."));
        var admission = new RazerSnapTapDriverAdmission(() => good with
        {
            ControllerObservationComplete = false,
            ControllerObservationFailure = observation,
            CommonDriver = new(true, false, null) { FailureDetail = package },
        });

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));
        Assert.Equal(RazerSnapTapAdmissionFailure.IncompleteControllerObservation, failure);
        Assert.Equal("ObserveControllers", detail?.Cause?.Operation);
        Assert.Equal("ProcessInspectionFailed", detail?.Cause?.Reason);
        Assert.DoesNotContain("private-owner", detail?.Cause?.Message, StringComparison.Ordinal);
        Assert.Equal("RzCommon", detail?.Cause?.Related?[0].Operation);
        Assert.Equal("CatalogTrustFailed", detail?.Cause?.Related?[0].Reason);
        Assert.True(detail?.IsValid());
    }

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
    public void BothInstalledPackagesMustBeTrusted(bool commonDriver)
    {
        RazerSnapTapDriverSnapshot snapshot = ExactSnapshot();
        snapshot = commonDriver
            ? snapshot with { CommonDriver = RazerDriverApiPackageEvidence.Rejected }
            : snapshot with { DeviceDriver = RazerDriverApiPackageEvidence.Rejected };

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
            CommonDriver = commonMissing ? RazerDriverApiPackageEvidence.Missing : exact.CommonDriver,
            DeviceDriver = deviceMissing ? RazerDriverApiPackageEvidence.Missing : exact.DeviceDriver,
            KeyboardDevices = [exact.KeyboardDevices[0] with { UpperFilters = [] }],
        };
        var admission = new RazerSnapTapDriverAdmission(
            () => snapshot,
            () => new RazerDriverInstallationInspection(
                Admitted: true, Installed: false, AdmissionFailure: RazerDriverInstallOutcome.Unavailable));

        Assert.False(admission.TryGetEndpoint(out _, out bool driverMissing));
        Assert.True(driverMissing);
        Assert.False(admission.TryGetEndpoint(out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UntrustedRemainingDriverDoesNotReportDriverMissing(bool commonMissing)
    {
        RazerSnapTapDriverSnapshot exact = ExactSnapshot();
        RazerSnapTapDriverSnapshot snapshot = exact with
        {
            CommonDriver = commonMissing ? RazerDriverApiPackageEvidence.Missing : RazerDriverApiPackageEvidence.Rejected,
            DeviceDriver = commonMissing ? RazerDriverApiPackageEvidence.Rejected : RazerDriverApiPackageEvidence.Missing,
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
            CommonDriver = RazerDriverApiPackageEvidence.Missing,
            DeviceDriver = RazerDriverApiPackageEvidence.Missing,
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
    public void RejectionReturnsBoundedSpecificDiagnostic()
    {
        RazerSnapTapDriverSnapshot snapshot = ExactSnapshot() with { ActiveConsoleSession = false };
        var admission = new RazerSnapTapDriverAdmission(() => snapshot);

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));

        Assert.Equal(RazerSnapTapAdmissionFailure.InactiveConsoleSession, failure);
        Assert.Equal("SnapTapAdmission", detail?.Component);
        Assert.Equal("Evaluate", detail?.Stage);
        Assert.Equal("InactiveConsoleSession", detail?.Reason);
        Assert.Null(detail?.ExceptionType);
    }

    [Fact]
    public void DiscoveryExceptionPreservesNativeInnerCauseAndRedactedMessage()
    {
        var admission = new RazerSnapTapDriverAdmission(() =>
            throw new IOException(@"Binding failed at C:\Users\owner-sensitive\driver.sys", new Win32Exception(5, "native message password=hidden-value")));

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));

        Assert.Equal(RazerSnapTapAdmissionFailure.DiscoveryFailed, failure);
        Assert.Equal(nameof(IOException), detail?.ExceptionType);
        Assert.Equal(nameof(Win32Exception), detail?.Cause?.ExceptionType);
        Assert.Equal(5, detail?.Cause?.NativeErrorCode);
        string serialized = System.Text.Json.JsonSerializer.Serialize(detail);
        Assert.DoesNotContain("owner-sensitive", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Binding failed", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-value", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native message", serialized, StringComparison.Ordinal);
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
        current = current with { DeviceDriver = new(true, true, "updated-trusted-build") };
        Assert.True(admission.TryGetEndpoint(out _));
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
            Assert.Equal(model.ModelNumber is "RZ09-0581" or "RZ09-0528",
                profile.AutomaticInstallationAvailable);
            Assert.Equal($@"HID\VID_1532&PID_{model.UsbProductId}&MI_01&COL01", profile.KeyboardHardwareId);
        }
    }

    public static IEnumerable<object[]> ReviewedProfiles() =>
        RazerSnapTapDriverProfiles.All.Select(profile => new object[] { profile.ProductId });

    [Theory]
    [MemberData(nameof(ReviewedProfiles))]
    public void EveryModelAcceptsTrustedUpdatesButInvalidatesThePreviousSession(string productId)
    {
        RazerSnapTapDriverSnapshot current = ModelSnapshot(productId);
        var admission = new RazerSnapTapDriverAdmission(() => current);
        Assert.True(admission.TryGetEndpoint(out var endpoint));
        current = current with
        {
            CommonDriver = new(true, true, "updated-trusted-common"),
            DeviceDriver = new(true, true, $"updated-trusted-{productId}"),
        };

        Assert.True(admission.TryGetEndpoint(out var updated));
        Assert.Equal(productId, updated.Profile.ProductId);
        Assert.False(admission.IsCurrent(endpoint));
        Assert.True(admission.IsCurrent(updated));
    }

    [Theory]
    [MemberData(nameof(ReviewedProfiles))]
    public void UntrustedPackageNeverBecomesMissingDriverInstallationAdvice(string productId)
    {
        RazerSnapTapDriverSnapshot current = ModelSnapshot(productId) with
        {
            DeviceDriver = RazerDriverApiPackageEvidence.Rejected,
        };
        var admission = new RazerSnapTapDriverAdmission(() => current);

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted, failure);
        Assert.False(admission.TryGetEndpoint(out _, out bool driverMissing));
        Assert.False(driverMissing);
    }

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
            Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot with { ControlDevices = foreign.ControlDevices }, out _));
            Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot with { KeyboardDevices = foreign.KeyboardDevices }, out _));
            Assert.False(RazerSnapTapDriverAdmission.TryAdmit(snapshot with { BiosProductName = foreign.BiosProductName }, out _));
        }
    }

    [Theory]
    [MemberData(nameof(ReviewedProfiles))]
    public void EveryModelRetainsFreshSessionControllerTrustAndTopologyGuards(string productId)
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
            good with { CommonDriver = RazerDriverApiPackageEvidence.Rejected },
            good with { DeviceDriver = RazerDriverApiPackageEvidence.Rejected },
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
        int inspections = 0;
        var admission = new RazerSnapTapDriverAdmission(
            () => good with
            {
                DeviceDriver = RazerDriverApiPackageEvidence.Missing,
                KeyboardDevices = [good.KeyboardDevices[0] with { UpperFilters = [] }],
            },
            () =>
            {
                inspections++;
                return new RazerDriverInstallationInspection(
                    Admitted: true, Installed: false, AdmissionFailure: RazerDriverInstallOutcome.Unavailable);
            });
        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
        Assert.Equal(productId is "02E0" or "02C6" ? RazerSnapTapAdmissionFailure.DriverMissing
            : RazerSnapTapAdmissionFailure.ManualDriverSetupRequired, failure);
        Assert.Equal(productId is "02E0" or "02C6" ? 1 : 0, inspections);
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    public void ExactInstallerInspectionOffersRepairForAnEligiblePartialStack(string productId)
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot(productId);
        int inspections = 0;
        var admission = new RazerSnapTapDriverAdmission(
            () => good with { ControlDevices = [] },
            () =>
            {
                inspections++;
                return new RazerDriverInstallationInspection(
                    Admitted: true, Installed: false, AdmissionFailure: RazerDriverInstallOutcome.Unavailable);
            });

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverMissing, failure);
        Assert.Equal(RazerSnapTapAdmissionFailure.ControlIdentityMismatch.ToString(), detail?.Reason);
        string context = Assert.IsType<string>(detail?.Context);
        Assert.Contains("\"ReportedFailure\":\"DriverMissing\"", context, StringComparison.Ordinal);
        Assert.Contains("\"InstallerSetupAvailable\":true", context, StringComparison.Ordinal);
        Assert.Equal(1, inspections);
    }

    [Fact]
    public void MissingPackageDiagnosticSurvivesSetupAvailabilityClassification()
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot("02C6");
        DiagnosticFailure package = DiagnosticFailure.FromException(
            "DriverPackage", "RzDev_02c6", "EndpointBinding", "EndpointDriverMissing",
            new InvalidDataException("The endpoint has no installed driver package."));
        var admission = new RazerSnapTapDriverAdmission(
            () => good with
            {
                DeviceDriver = RazerDriverApiPackageEvidence.Missing with { FailureDetail = package },
                KeyboardDevices = [good.KeyboardDevices[0] with { UpperFilters = [] }],
            },
            () => new RazerDriverInstallationInspection(true, false, RazerDriverInstallOutcome.Unavailable));

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverMissing, failure);
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverSetupRequired.ToString(), detail?.Reason);
        Assert.Equal("RzDev_02c6", detail?.Cause?.Operation);
        Assert.Equal("EndpointDriverMissing", detail?.Cause?.Reason);
    }

    [Fact]
    public void RefusedOrAlreadyInstalledInspectionDoesNotOfferRepair()
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot("02C6") with { ControlDevices = [] };
        foreach (RazerDriverInstallationInspection inspection in new[]
        {
            new RazerDriverInstallationInspection(false, false, RazerDriverInstallOutcome.Unavailable),
            new RazerDriverInstallationInspection(true, true, RazerDriverInstallOutcome.Installed),
        })
        {
            var admission = new RazerSnapTapDriverAdmission(() => good, () => inspection);

            Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
            Assert.Equal(RazerSnapTapAdmissionFailure.ControlIdentityMismatch, failure);
        }
    }

    [Fact]
    public void UntrustedPackageNeverInvokesInstallerInspection()
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot("02C6");
        int inspections = 0;
        var admission = new RazerSnapTapDriverAdmission(
            () => good with { DeviceDriver = RazerDriverApiPackageEvidence.Rejected },
            () =>
            {
                inspections++;
                return new RazerDriverInstallationInspection(true, false, RazerDriverInstallOutcome.Unavailable);
            });

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted, failure);
        Assert.Equal(0, inspections);
    }

    [Theory]
    [InlineData("02E0")]
    [InlineData("02C6")]
    public void KnownInboxBindingGapOffersExactInstallerWithoutLosingPackageFailures(string productId)
    {
        RazerSnapTapDriverSnapshot snapshot = OwnerInboxBindingSnapshot(productId);
        int inspections = 0;
        var admission = new RazerSnapTapDriverAdmission(
            () => snapshot,
            () =>
            {
                inspections++;
                return new RazerDriverInstallationInspection(true, false, RazerDriverInstallOutcome.Unavailable);
            });

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverMissing, failure);
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted.ToString(), detail?.Reason);
        Assert.Equal("RzCommon", detail?.Cause?.Operation);
        Assert.Equal("EndpointBinding", detail?.Cause?.Stage);
        Assert.Equal("EndpointMissing", detail?.Cause?.Reason);
        Assert.Equal($"RzDev_{productId.ToLowerInvariant()}", detail?.Cause?.Related?[0].Operation);
        Assert.Equal("InfContract", detail?.Cause?.Related?[0].Stage);
        Assert.Equal("ProviderMismatch", detail?.Cause?.Related?[0].Reason);
        Assert.Equal(1, inspections);
    }

    [Fact]
    public void RefusedInspectionKeepsKnownInboxBindingGapUntrusted()
    {
        RazerSnapTapDriverSnapshot snapshot = OwnerInboxBindingSnapshot("02C6");
        var admission = new RazerSnapTapDriverAdmission(
            () => snapshot,
            () => new RazerDriverInstallationInspection(false, false, RazerDriverInstallOutcome.Unavailable));

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted, failure);
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted.ToString(), detail?.Reason);
        Assert.Equal("EndpointMissing", detail?.Cause?.Reason);
    }

    [Fact]
    public void AcPowerInspectionRefusalPreservesConcreteReasonWithoutOfferingInstall()
    {
        RazerSnapTapDriverSnapshot snapshot = OwnerInboxBindingSnapshot("02C6");
        var admission = new RazerSnapTapDriverAdmission(
            () => snapshot,
            () => new RazerDriverInstallationInspection(
                false, false, RazerDriverInstallOutcome.AcPowerRequired));

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted, failure);
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted.ToString(), detail?.Reason);
        DiagnosticFailure refusal = Assert.Single(detail?.Related ?? []);
        Assert.Equal("SnapTapAdmission", refusal.Component);
        Assert.Equal("InspectDriverSetup", refusal.Operation);
        Assert.Equal("InstallerAdmission", refusal.Stage);
        Assert.Equal(RazerDriverInstallOutcome.AcPowerRequired.ToString(), refusal.Reason);
        string context = Assert.IsType<string>(refusal.Context);
        Assert.Contains("\"Admitted\":false", context, StringComparison.Ordinal);
        Assert.Contains("\"Installed\":false", context, StringComparison.Ordinal);
        Assert.Contains("\"Outcome\":\"AcPowerRequired\"", context, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogTrustFailureNeverInvokesInstallerInspection()
    {
        RazerSnapTapDriverSnapshot snapshot = OwnerInboxBindingSnapshot("02C6") with
        {
            CommonDriver = new(true, false, null)
            {
                FailureDetail = new DiagnosticFailure(
                    "DriverPackage", "RzCommon", "CatalogTrust", "CatalogTrustFailed"),
            },
        };
        int inspections = 0;
        var admission = new RazerSnapTapDriverAdmission(
            () => snapshot,
            () =>
            {
                inspections++;
                return new RazerDriverInstallationInspection(true, false, RazerDriverInstallOutcome.Unavailable);
            });

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted, failure);
        Assert.Equal(0, inspections);
    }

    [Theory]
    [InlineData("ActiveDriver", "ActiveDriverMismatch")]
    [InlineData("DriverComparison", "ActiveDriverMismatch")]
    [InlineData("DriverComparison", "ActiveImageMismatch")]
    [InlineData("EndpointBinding", "UnknownFailure")]
    [InlineData("InfContract", "ProviderMismatch")]
    public void UnsafeCommonPackageFailuresNeverInvokeInstallerInspection(string stage, string reason)
    {
        RazerSnapTapDriverSnapshot snapshot = OwnerInboxBindingSnapshot("02C6") with
        {
            CommonDriver = new(true, false, null)
            {
                FailureDetail = new DiagnosticFailure("DriverPackage", "RzCommon", stage, reason),
            },
        };
        int inspections = 0;
        var admission = new RazerSnapTapDriverAdmission(
            () => snapshot,
            () =>
            {
                inspections++;
                return new RazerDriverInstallationInspection(true, false, RazerDriverInstallOutcome.Unavailable);
            });

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted, failure);
        Assert.Equal(0, inspections);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"PublishedInfName\":\"other.inf\"}")]
    [InlineData("{\"publishedInfName\":\"keyboard.inf\"}")]
    [InlineData("{\"PublishedInfName\":\"keyboard.inf\",\"PublishedInfName\":\"keyboard.inf\"}")]
    public void MalformedOrNonInboxProviderContextNeverInvokesInstallerInspection(string? context)
    {
        RazerSnapTapDriverSnapshot snapshot = OwnerInboxBindingSnapshot("02C6");
        snapshot = snapshot with
        {
            DeviceDriver = snapshot.DeviceDriver with
            {
                FailureDetail = snapshot.DeviceDriver.FailureDetail! with { Context = context },
            },
        };
        int inspections = 0;
        var admission = new RazerSnapTapDriverAdmission(
            () => snapshot,
            () =>
            {
                inspections++;
                return new RazerDriverInstallationInspection(true, false, RazerDriverInstallOutcome.Unavailable);
            });

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
        Assert.Equal(RazerSnapTapAdmissionFailure.DriverPackageUntrusted, failure);
        Assert.Equal(0, inspections);
    }

    [Fact]
    public void ModelsWithoutAnExactRecipeRemainManualEvenWhenInspectionWouldAdmit()
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot("029F");
        int inspections = 0;
        var admission = new RazerSnapTapDriverAdmission(
            () => good with
            {
                DeviceDriver = RazerDriverApiPackageEvidence.Missing,
                KeyboardDevices = [good.KeyboardDevices[0] with { UpperFilters = [] }],
            },
            () =>
            {
                inspections++;
                return new RazerDriverInstallationInspection(true, false, RazerDriverInstallOutcome.Unavailable);
            });

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
        Assert.Equal(RazerSnapTapAdmissionFailure.ManualDriverSetupRequired, failure);
        Assert.Equal(0, inspections);
    }

    [Fact]
    public void HostSessionAndControllerFailuresNeverInvokeInstallerInspection()
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot("02C6");
        RazerSnapTapDriverSnapshot[] rejected =
        [
            good with { BiosProductName = "Unsupported model" },
            good with { ActiveConsoleSession = false },
            good with { ControllerObservationComplete = false },
            good with { CompetingControllerDetected = true },
        ];
        foreach (RazerSnapTapDriverSnapshot snapshot in rejected)
        {
            int inspections = 0;
            var admission = new RazerSnapTapDriverAdmission(
                () => snapshot,
                () =>
                {
                    inspections++;
                    return new RazerDriverInstallationInspection(true, false, RazerDriverInstallOutcome.Unavailable);
                });

            Assert.False(admission.TryGetEndpointWithFailure(out _, out _));
            Assert.Equal(0, inspections);
        }
    }

    [Fact]
    public void CaptureOnlyTestsNeverPerformLiveInstallerInspection()
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot("02C6") with { KeyboardDevices = [] };
        var admission = new RazerSnapTapDriverAdmission(() => good);

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure));
        Assert.Equal(RazerSnapTapAdmissionFailure.KeyboardIdentityMismatch, failure);
    }

    [Fact]
    public void InstallerInspectionFailurePreservesTheOriginalAdmissionFailure()
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot("02C6") with { ControlDevices = [] };
        var admission = new RazerSnapTapDriverAdmission(
            () => good,
            () => throw new IOException("Installer inspection failed."));

        Assert.False(admission.TryGetEndpointWithFailure(out _, out var failure, out var detail));
        Assert.Equal(RazerSnapTapAdmissionFailure.ControlIdentityMismatch, failure);
        Assert.Equal(RazerSnapTapAdmissionFailure.ControlIdentityMismatch.ToString(), detail?.Reason);
        Assert.Equal("InspectDriverSetup", detail?.Related?[0].Operation);
        Assert.Equal("InspectionFailed", detail?.Related?[0].Reason);
    }

    [Fact]
    public void ModelProfilesSelectTheReviewedVendorFiltersAndKeepValidationProvenanceSeparate()
    {
        RazerSnapTapDriverProfile blade16 = Assert.Single(RazerSnapTapDriverProfiles.All, p => p.ProductId == "02C6");
        Assert.Equal("Blade 16 - RZ09-0528", blade16.ProductName);
        Assert.Equal("RzDev_02c6.sys", blade16.DeviceFileName);
        Assert.True(blade16.AutomaticInstallationAvailable);
        Assert.False(blade16.PhysicallyValidated);
        Assert.True(Assert.Single(RazerSnapTapDriverProfiles.All, p => p.ProductId == "02E0").PhysicallyValidated);
    }

    private static RazerSnapTapDriverSnapshot OwnerInboxBindingSnapshot(string productId)
    {
        RazerSnapTapDriverSnapshot good = ModelSnapshot(productId);
        var commonFailure = new DiagnosticFailure(
            "DriverPackage", "RzCommon", "EndpointBinding", "EndpointMissing");
        var deviceFailure = new DiagnosticFailure(
            "DriverPackage", $"RzDev_{productId.ToLowerInvariant()}", "InfContract", "ProviderMismatch")
        {
            Context = System.Text.Json.JsonSerializer.Serialize(new
            {
                PublishedInfName = "keyboard.inf",
            }),
        };
        return good with
        {
            CommonDriver = new(true, false, null) { FailureDetail = commonFailure },
            DeviceDriver = new(true, false, null) { FailureDetail = deviceFailure },
        };
    }

    private static RazerSnapTapDriverSnapshot ModelSnapshot(string productId)
    {
        var profile = Assert.Single(RazerSnapTapDriverProfiles.All, p => p.ProductId == productId);
        var exact = ExactSnapshot();
        return exact with
        {
            BiosProductName = profile.ProductName,
            SystemProductName = profile.ProductName,
            DeviceDriver = new(true, true, $"trusted-{productId}-device-build"),
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
        new RazerDriverApiPackageEvidence(true, true, "current-common-build"),
        new RazerDriverApiPackageEvidence(true, true, "current-device-build"),
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
