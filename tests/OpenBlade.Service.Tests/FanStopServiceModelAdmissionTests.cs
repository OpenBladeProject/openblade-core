using System.Runtime.InteropServices;
using System.ServiceProcess;
using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.FanStop.Guardian;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed class FanStopServiceModelAdmissionTests
{
    private const int ServicePid = 42;
    private static readonly long ServiceStart = DateTime.UnixEpoch.Ticks;

    [Fact]
    public void EveryReviewedContractAdmitsOnlyItsOwnHostHidEcAndJournal()
    {
        foreach (FanStopModelContract contract in FanStopModelContract.All)
        {
            var identity = Journal(contract);
            var metadata = Metadata(contract);
            Assert.Equal(So6120AdmissionAssessment.InputsMatched, Assess(contract, identity, metadata));
            foreach (FanStopModelContract other in FanStopModelContract.All.Where(item => item != contract))
            {
                if (!contract.MatchesHost(Metadata(other).Host))
                {
                    Assert.Equal(So6120AdmissionAssessment.HostMismatch,
                        Assess(contract, identity, metadata with { Host = Metadata(other).Host }));
                }
                if (contract.EcVersion != other.EcVersion)
                {
                    Assert.Equal(So6120AdmissionAssessment.EcVersionMismatch,
                        Assess(contract, identity, metadata with { ReportedEcVersion = other.EcVersion }));
                }
                if (contract.ProductId != other.ProductId)
                {
                    Assert.Equal(So6120AdmissionAssessment.HidIdentityUnavailable,
                        Assess(contract, identity, metadata with { HidCandidates = Metadata(other).HidCandidates }));
                }
                Assert.Equal(So6120AdmissionAssessment.OpenBladeHandoffMissingOrMismatched,
                    Assess(contract, Journal(other), metadata));
            }
        }
    }

    [Fact]
    public void GuardianChoosesAnIntendedFirmwareCandidateFromFreshReportedVersion()
    {
        foreach (FanStopModelContract contract in FanStopModelContract.All)
        {
            FanStopModelContract family = FanStopModelContract.ForModel(contract.Model)!;
            Assert.Same(contract, So6120ServiceModelAdmission.SelectFirmware(family, FirmwareMetadata(contract)));
            Assert.Throws<InvalidDataException>(() => So6120ServiceModelAdmission.SelectFirmware(family,
                FirmwareMetadata(contract) with { ReportedEcVersion = new Version(99, 99) }));
            foreach (FanStopModelContract other in FanStopModelContract.All.Where(item => item.Model != family.Model))
            {
                Assert.Throws<InvalidDataException>(() => So6120ServiceModelAdmission.SelectFirmware(family,
                    FirmwareMetadata(other) with { ReportedEcVersion = contract.EcVersion }));
            }
        }
        var standard = FanStopModelContract.ForFirmware("RZ09-0483", new Version(2, 1))!;
        var lamborghini = FanStopModelContract.ForFirmware("RZ09-0483", new Version(1, 1))!;
        Assert.Equal((ushort)0x3006, standard.BoardSsid);
        Assert.Equal((ushort)0x3009, lamborghini.BoardSsid);
        Assert.NotEqual(Journal(standard).FirmwareEvidenceSha256, Journal(lamborghini).FirmwareEvidenceSha256);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("firmware")]
    [InlineData("imageHash")]
    [InlineData("contractHash")]
    [InlineData("operation")]
    [InlineData("session")]
    [InlineData("consent")]
    [InlineData("consentHash")]
    [InlineData("service")]
    [InlineData("profile")]
    [InlineData("duty")]
    public void JournalCannotSubstituteModelFirmwareOwnerOrRequestedOperation(string defect)
    {
        foreach (FanStopModelContract contract in FanStopModelContract.All)
        {
            So6120JournalIdentity identity = Journal(contract);
            identity = defect switch
            {
                "device" => identity with { Owner = identity.Owner with { DeviceIdentity = "RZ09-FFFF/1532:FFFF" } },
                "firmware" => identity with { Owner = identity.Owner with { FirmwareEvidence = "unreviewed" } },
                "imageHash" => identity with { FirmwareEvidenceSha256 = new('A', 64) },
                "contractHash" => identity with { DeviceEvidenceSha256 = new('A', 64) },
                "operation" => identity with { Owner = identity.Owner with { OperationId = Guid.Empty } },
                "session" => identity with { Owner = identity.Owner with { ControllerSession = Guid.Empty } },
                "consent" => identity with { ConsentId = Guid.NewGuid() },
                "consentHash" => identity with { ConsentSha256 = new('A', 64) },
                "service" => identity with { BootIdentity = $"service:{ServicePid + 1}:{ServiceStart}" },
                "profile" => identity with { CapturedProfile = PerformancePreset.Silent },
                _ => identity with { RequestedDuty1 = 1 },
            };
            Assert.Equal(So6120AdmissionAssessment.OpenBladeHandoffMissingOrMismatched,
                Assess(contract, identity, Metadata(contract)));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unreadable")]
    [InlineData("wrongPath")]
    [InlineData("wrongIdentity")]
    [InlineData("wrongUsage")]
    [InlineData("shortReport")]
    public void MissingAmbiguousOrMismatchedControlCollectionRefusesAdmission(string defect)
    {
        foreach (FanStopModelContract contract in FanStopModelContract.All)
        {
            So6120SupervisedMetadata metadata = Metadata(contract);
            So6120HidMetadata control = metadata.HidCandidates[0];
            metadata = metadata with
            {
                HidCandidates = defect switch
                {
                    "missing" => [],
                    "duplicate" => [control, control with { Path = control.Path + "2" }],
                    "unreadable" => [control, new(control.Path + "2", null, 5)],
                    "wrongPath" => [control with { Path = @"\\?\hid#vid_1532&pid_ffff#fixture" }],
                    "wrongIdentity" => [control with { Identity = new(0x1532, 0xFFFF, 1, 2, 91) }],
                    "wrongUsage" => [control with { Identity = new(contract.VendorId, contract.ProductId, 1, 1, 91) }],
                    _ => [control with { Identity = new(contract.VendorId, contract.ProductId, 1, 2, 90) }],
                },
            };
            Assert.Equal(So6120AdmissionAssessment.HidIdentityUnavailable, Assess(contract, Journal(contract), metadata));
        }
    }

    [Fact]
    public void ReadableSiblingCollectionDoesNotInventAnotherControlCollection()
    {
        foreach (FanStopModelContract contract in FanStopModelContract.All)
        {
            So6120SupervisedMetadata metadata = Metadata(contract);
            metadata = metadata with
            {
                HidCandidates = [.. metadata.HidCandidates,
                    new(PathFor(contract) + "2", new(contract.VendorId, contract.ProductId, 1, 1, 8))],
            };
            Assert.Equal(So6120AdmissionAssessment.InputsMatched, Assess(contract, Journal(contract), metadata));
        }
    }

    [Fact]
    public void SourceReviewedProductDoesNotBroadenSupervisedDeveloperScope()
    {
        foreach (FanStopModelContract contract in FanStopModelContract.All.Where(item => item.Model != "RZ09-0581"))
        {
            var metadata = Metadata(contract);
            Assert.Equal(So6120AdmissionAssessment.HostMismatch,
                So6120SupervisedAdmission.Assess(TimeProvider.System, metadata, null, null));
            Assert.False(So6120SupervisedAdmission.IsTargetHidPath(PathFor(contract)));
        }
        Assert.Null(FanStopModelContract.ForModel("RZ09-FFFF"));
    }

    private static So6120AdmissionAssessment Assess(FanStopModelContract contract, So6120JournalIdentity identity,
        So6120SupervisedMetadata metadata) =>
        So6120ServiceModelAdmission.Assess(contract, identity, ServicePid, ServiceStart, metadata);

    private static So6120JournalIdentity Journal(FanStopModelContract contract) =>
        So6120ServiceModelAdmission.CreateJournal(contract, Guid.NewGuid(), ServicePid, ServiceStart);

    private static So6120ServiceFirmwareMetadata FirmwareMetadata(FanStopModelContract contract) =>
        new(Architecture.X64, Metadata(contract).Host, contract.EcVersion);

    private static string PathFor(FanStopModelContract contract) =>
        $@"\\?\hid#vid_{contract.VendorId:x4}&pid_{contract.ProductId:x4}&mi_03&col01#fixture";

    private static So6120SupervisedMetadata Metadata(FanStopModelContract contract)
    {
        long now = TimeProvider.System.GetTimestamp();
        return new(new(Guid.NewGuid(), 1, TimeProvider.System, now), now, Architecture.X64,
            new(new HostIdentityEvidence(contract.Host.Manufacturer, contract.Host.ProductName, "variant", "9.99"), null),
            contract.EcVersion, [new(PathFor(contract), new(contract.VendorId, contract.ProductId, 1, 2, 91))],
            new(false, 0, 1, 1),
            new(@"C:\Windows", @"\SystemRoot\System32\Drivers\inpoutx64.sys",
                @"C:\Windows\System32\Drivers\inpoutx64.sys", ServiceType.KernelDriver, ServiceControllerStatus.Running,
                15008, So6120SupervisedAdmission.DriverHash, @"\Device\inpoutx64"),
            [], ServiceControllerStatus.Running, [ServicePid]);
    }
}
