using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class So6120SupervisedAdmissionTests
{
    private const string ControlHidPath = @"\\?\hid#vid_1532&pid_02e0&mi_03&col01#fixture";
    private const string SecondHidPath = @"\\?\hid#vid_1532&pid_02e0&mi_03&col02#fixture";
    private const string OtherHidPath = @"\\?\hid#vid_1532&pid_02e1&mi_03&col01#fixture";
    private const So6120AcceptedResiduals Residuals = So6120AcceptedResiduals.UnnamedFileHandles
        | So6120AcceptedResiduals.UncoveredAcpiPaths | So6120AcceptedResiduals.UnobservedWriters;
    private static readonly HidHandleIdentity ControlIdentity = new(0x1532, 0x02E0, 1, 2, 91);

    [Fact]
    public void AlreadyStoppedHandoffNeedsNoInventedProcessExit()
    {
        var fixture = new Fixture();
        Assert.Equal(So6120AdmissionAssessment.InputsMatched, fixture.Assess());
    }

    [Fact]
    public void ThisRunStoppedAnOriginalProcessOnlyWithAnObservedExit()
    {
        var fixture = new Fixture();
        fixture.Handoff = fixture.Handoff with { Origin = So6120HandoffOrigin.StoppedByThisRun };
        Assert.Equal(So6120AdmissionAssessment.OpenBladeHandoffMissingOrMismatched, fixture.Assess());
        fixture.Handoff = fixture.Handoff with { OriginalExit = new(765, DateTime.UnixEpoch.Ticks, fixture.Handoff.Stamp.Timestamp) };
        Assert.Equal(So6120AdmissionAssessment.InputsMatched, fixture.Assess());
        fixture.Metadata = fixture.Metadata with { OpenBladeWriterPids = [765] };
        Assert.Equal(So6120AdmissionAssessment.OpenBladeHandoffMissingOrMismatched, fixture.Assess());
    }

    [Theory]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("clock")]
    [InlineData("negative")]
    public void MetadataUsesOneFreshMonotonicClock(string defect)
    {
        var fixture = new Fixture();
        var metadata = fixture.Metadata;
        fixture.Metadata = defect switch
        {
            "future" => metadata with { Stamp = metadata.Stamp with { Timestamp = fixture.Clock.Ticks + 1 } },
            "stale" => metadata with { StartedTimestamp = fixture.Clock.Ticks - TimeSpan.FromSeconds(6).Ticks },
            "clock" => metadata with { Stamp = metadata.Stamp with { Clock = new TestClock() } },
            _ => metadata with { StartedTimestamp = -1 },
        };
        Assert.Equal(So6120AdmissionAssessment.StaleMetadata, fixture.Assess());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("scope")]
    [InlineData("run")]
    [InlineData("session")]
    [InlineData("residual")]
    public void ReviewedCallerEvidenceRequiresItsOwnExactCurrentContext(string defect)
    {
        var fixture = new Fixture();
        var challenge = fixture.Challenge!;
        fixture.Challenge = defect switch
        {
            "missing" => null,
            "future" => challenge with { Stamp = challenge.Stamp with { Timestamp = fixture.Clock.Ticks + 1 } },
            "stale" => challenge with { Stamp = challenge.Stamp with { Timestamp = fixture.Clock.Ticks - TimeSpan.FromMinutes(16).Ticks } },
            "scope" => challenge with { ObservedScope = challenge.ObservedScope with { PeripheralConfigurationSha256 = new('E', 64) } },
            "run" => challenge with { Stamp = challenge.Stamp with { RunId = Guid.NewGuid() } },
            "session" => challenge with { Stamp = challenge.Stamp with { ConsoleSession = 2 } },
            _ => challenge with { AcceptedResiduals = So6120AcceptedResiduals.UnobservedWriters },
        };
        Assert.Equal(So6120AdmissionAssessment.CallerChallengeMissingOrMismatched, fixture.Assess());
    }

    [Fact]
    public void KnownControllerCannotBeWaivedByCallerChallengeAcceptance()
    {
        var fixture = new Fixture();
        fixture.Metadata = fixture.Metadata with { DetectedControllers = ["RazerAppEngine"] };
        Assert.Equal(So6120AdmissionAssessment.ControllerDetected, fixture.Assess());
    }

    [Theory]
    [InlineData("Razer Synapse Service", true)]
    [InlineData("razersynapseservice", true)]
    [InlineData("Razer Chroma SDK Service", true)]
    [InlineData("RGS", true)]
    [InlineData("GameManagerService3", true)]
    [InlineData("Razer Synapse Service Helper", false)]
    [InlineData("OpenBlade.Service", false)]
    public void ProcessInventoryRecognizesExactAliasesWithoutScmRegistration(string process, bool expected) =>
        Assert.Equal(expected, So6120WindowsSupervisedMetadata.IsKnownControllerProcess(process));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(255, 1)]
    [InlineData(1, 2)]
    public void RefusesOtherPowerOrConsole(int acLine, int processSession)
    {
        var fixture = new Fixture();
        fixture.Metadata = fixture.Metadata with
        {
            Session = fixture.Metadata.Session with { AcLineStatus = (byte)acLine, ProcessSession = processSession },
        };
        Assert.Equal(So6120AdmissionAssessment.SessionOrPowerMismatch, fixture.Assess());
    }

    [Theory]
    [InlineData("host")]
    [InlineData("ec")]
    [InlineData("path")]
    [InlineData("type")]
    [InlineData("stopped")]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("alias")]
    public void RequiresExactHostAndRunningDriverEvidence(string defect)
    {
        var fixture = new Fixture();
        var metadata = fixture.Metadata;
        var driver = metadata.Driver;
        fixture.Metadata = defect switch
        {
            "host" => metadata with { Host = new(new HostIdentityEvidence("Razer", "Blade 16 - RZ09-0528", "RZ09-0528", "4.01"), null) },
            "ec" => metadata with { ReportedEcVersion = new Version(1, 5) },
            "path" => metadata with { Driver = driver with { ConfiguredPath = @"C:\temp\inpoutx64.sys" } },
            "type" => metadata with { Driver = driver with { ServiceType = ServiceType.FileSystemDriver } },
            "stopped" => metadata with { Driver = driver with { ServiceStatus = ServiceControllerStatus.Stopped } },
            "size" => metadata with { Driver = driver with { Bytes = 15009 } },
            "hash" => metadata with { Driver = driver with { Sha256 = new('A', 64) } },
            _ => metadata with { Driver = driver with { DosTarget = @"\Device\inpoutx64\suffix" } },
        };
        var expected = defect switch
        {
            "host" => So6120AdmissionAssessment.HostMismatch,
            "ec" => So6120AdmissionAssessment.EcVersionMismatch,
            _ => So6120AdmissionAssessment.DriverMismatch,
        };
        Assert.Equal(expected, fixture.Assess());
    }

    [Fact]
    public void MetadataOnlyHidCollectionKeepsAnUnreadableRelevantCandidate()
    {
        var native = new HidFixture();
        var paths = So6120WindowsSupervisedMetadata.ParsePresentHidPaths($"{OtherHidPath}\0{ControlHidPath}\0{SecondHidPath}\0\0");
        var candidates = So6120WindowsSupervisedMetadata.ReadHidMetadata(paths, native);
        Assert.Equal(new[] { ControlHidPath, SecondHidPath }, native.Opened);
        Assert.Null(candidates[0].OpenError);
        Assert.Null(candidates[1].Identity);
        Assert.Equal(5, candidates[1].OpenError);
        var fixture = new Fixture();
        fixture.Metadata = fixture.Metadata with { HidCandidates = candidates };
        Assert.Equal(So6120AdmissionAssessment.HidIdentityUnavailable, fixture.Assess());
        fixture.Metadata = fixture.Metadata with { HidCandidates = [new(ControlHidPath, ControlIdentity), new(SecondHidPath, ControlIdentity)] };
        Assert.Equal(So6120AdmissionAssessment.HidIdentityUnavailable, fixture.Assess());
    }

    [Fact]
    public void IncompleteHidListCannotReturnAPartialTargetSet()
    {
        Assert.Throws<IOException>(() => So6120WindowsSupervisedMetadata.ParsePresentHidPaths($"{ControlHidPath}\0"));
        Assert.Throws<IOException>(() => So6120WindowsSupervisedMetadata.ParsePresentHidPaths($"{ControlHidPath}\0\0{SecondHidPath}\0\0"));
    }

    [Fact]
    public void SmbiosUsesTypeZeroReportedVersionAndRejectsUnknownOrMalformedData()
    {
        byte[] table = new byte[0x18 + 2 + 4 + 2];
        table[1] = 0x18;
        table[0x16] = 1;
        table[0x17] = 4;
        table[0x1A] = 127;
        table[0x1B] = 4;
        Assert.Equal(new Version(1, 4), So6120WindowsSupervisedMetadata.ReadReportedEcVersion(Raw(table)));
        Assert.Null(So6120WindowsSupervisedMetadata.ReadReportedEcVersion(Raw(table[..^1])));
        Assert.Null(So6120WindowsSupervisedMetadata.ReadReportedEcVersion(Raw([.. table[..0x1A], .. table])));
        table[0x17] = 0xFF;
        Assert.Null(So6120WindowsSupervisedMetadata.ReadReportedEcVersion(Raw(table)));
    }

    private static byte[] Raw(byte[] table)
    {
        byte[] raw = new byte[8 + table.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)table.Length);
        table.CopyTo(raw, 8);
        return raw;
    }

    private sealed class TestClock : TimeProvider
    {
        public long Ticks { get; } = TimeSpan.FromHours(1).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    private sealed class Fixture
    {
        public TestClock Clock { get; } = new();
        public So6120SupervisedMetadata Metadata { get; set; }
        public So6120CallerChallengeContext? Challenge { get; set; }
        public So6120OpenBladeHandoff Handoff { get; set; }

        public Fixture()
        {
            var stamp = new So6120AdmissionStamp(Guid.NewGuid(), 1, Clock, Clock.Ticks - 1);
            var driver = new So6120DriverMetadata(@"C:\Windows", @"\SystemRoot\System32\Drivers\inpoutx64.sys",
                @"C:\Windows\System32\Drivers\inpoutx64.sys", ServiceType.KernelDriver, ServiceControllerStatus.Running,
                15008, So6120SupervisedAdmission.DriverHash, @"\Device\inpoutx64");
            Metadata = new(stamp, Clock.Ticks - 2, Architecture.X64,
                new(new HostIdentityEvidence("Razer", "Blade 16 - RZ09-0581", "RZ09-05819EN4", "4.01"), null),
                new Version(1, 4), [new(ControlHidPath, ControlIdentity)], new(true, 1, 1, 1), driver,
                [], ServiceControllerStatus.Stopped, []);
            var scope = new So6120ChallengeScope(ControlHidPath, "4.01", new Version(1, 4),
                So6120SupervisedAdmission.DriverHash, new('A', 64), new('B', 64));
            var context = stamp with { Timestamp = Clock.Ticks - 3 };
            Challenge = new(context, Guid.NewGuid(), new('C', 64), scope, scope, Residuals, Residuals);
            Handoff = new(context, So6120HandoffOrigin.AlreadyStopped, null);
        }

        public So6120AdmissionAssessment Assess() => So6120SupervisedAdmission.Assess(Clock, Metadata, Challenge, Handoff);
    }

    private sealed class HidFixture : IHidCandidateNative
    {
        public List<string> Opened { get; } = [];
        public IReadOnlyList<string> EnumerateDevicePaths() => throw new NotSupportedException();
        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters)
        {
            Opened.Add(path);
            Assert.Equal(new HidOpenParameters(0, 3, 3, 0, false), parameters);
            return path == ControlHidPath
                ? new(new OpenedCandidateHandle(new SafeFileHandle(new IntPtr(1234), ownsHandle: false)), null)
                : new(null, 5);
        }
        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) => ControlIdentity;
        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle) => throw new NotSupportedException();
    }
}
