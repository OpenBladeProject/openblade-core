using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class So6120WindowsDirectDutyPlatformTests
{
    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Balanced)]
    public void OwnsOneHandleAndPinUntilAnExplicitClose(PerformancePreset profile)
    {
        var fixture = new Fixture(profile);
        using var acquired = So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native);
        Assert.Equal(new[] { "reserve", "owners", "metadata", "owners", "metadata", "pin", "latch", "owners", "metadata", "owners", "open" },
            fixture.Native.Calls);
        Assert.Equal(So6120HandoffOrigin.AlreadyStopped, acquired.Binding.Handoff.Origin);
        Assert.Equal(profile, acquired.Binding.Identity.CapturedProfile);
        Assert.Null(acquired.Binding.Handoff.OriginalExit);
        Assert.Equal(So6120NativeCloseState.NotAttempted, acquired.CloseEvidence.State);
        Assert.Throws<InvalidOperationException>(() => So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native));
        acquired.Dispose();
        acquired.Dispose();
        Assert.Equal(new[] { "close", "unpin", "unlatch", "release" }, fixture.Native.Calls.TakeLast(4));
        Assert.Equal(1, fixture.Native.Calls.Count(call => call == "close"));
        Assert.Equal(So6120NativeCloseState.Closed, acquired.CloseEvidence.State);
        Assert.Equal(0, acquired.CloseEvidence.NativeError);
    }

    [Fact]
    public void UnsupportedProfileCannotAcquireAnAttendedBaseline()
    {
        var fixture = new Fixture(PerformancePreset.Performance);
        Assert.Throws<InvalidOperationException>(() => So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native));
        Assert.Empty(fixture.Native.Calls);
    }

    [Fact]
    public void ActiveUserOwnerAndActiveServiceBothRefuseBeforeAcquisition()
    {
        var activeTray = new Fixture();
        activeTray.Native.UserOwnersStopped = false;
        Assert.Throws<InvalidOperationException>(() => So6120WindowsDirectDutyPlatform.Acquire(activeTray.Consent, activeTray.Native));
        Assert.Equal(new[] { "reserve", "owners", "release" }, activeTray.Native.Calls);

        var activeService = new Fixture();
        activeService.Native.Metadata = activeService.Native.Metadata with { OpenBladeStatus = ServiceControllerStatus.Running };
        Assert.Throws<InvalidOperationException>(() => So6120WindowsDirectDutyPlatform.Acquire(activeService.Consent, activeService.Native));
        Assert.Equal(new[] { "reserve", "owners", "metadata", "release" }, activeService.Native.Calls);
    }

    [Fact]
    public void ChangedScopeAfterPinClosesOnlyTheFileAndNeverOpensTheDevice()
    {
        var fixture = new Fixture();
        fixture.Native.AfterPin = () => fixture.CurrentScope = fixture.CurrentScope with { ControllerSoftwareSha256 = new('F', 64) };
        Assert.Throws<InvalidOperationException>(() => So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native));
        Assert.Equal(new[] { "unpin", "unlatch", "release" }, fixture.Native.Calls.TakeLast(3));
        Assert.DoesNotContain("open", fixture.Native.Calls);
        Assert.DoesNotContain("close", fixture.Native.Calls);
    }

    [Fact]
    public void NativeOpenFailurePreservesErrorAndConsumesTheAttempt()
    {
        var fixture = new Fixture();
        fixture.Native.OpenError = 5;
        var error = Assert.Throws<Win32Exception>(() => So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(new[] { "open", "unpin", "unlatch", "release" }, fixture.Native.Calls.TakeLast(4));
        Assert.Throws<InvalidOperationException>(() => So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native));
        Assert.Equal(1, fixture.Native.Calls.Count(call => call == "open"));
    }

    [Fact]
    public void CancellationAfterPinPreventsDeviceOpenAndReleasesFile()
    {
        var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        fixture.Native.AfterPin = cancel.Cancel;
        Assert.Throws<OperationCanceledException>(() => So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native, cancel.Token));
        Assert.Equal(new[] { "unpin", "release" }, fixture.Native.Calls.TakeLast(2));
        Assert.DoesNotContain("latch", fixture.Native.Calls);
        Assert.DoesNotContain("open", fixture.Native.Calls);
    }

    [Fact]
    public void CancellationDuringSynchronousOpenReturnsTheHandleForObservedClose()
    {
        var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        fixture.Native.AfterOpen = cancel.Cancel;
        using var acquired = So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native, cancel.Token);
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(So6120NativeCloseState.NotAttempted, acquired.CloseEvidence.State);
        acquired.Dispose();
        Assert.Equal(So6120NativeCloseState.Closed, acquired.CloseEvidence.State);
    }

    [Fact]
    public void FailedCloseIsReportedWithoutReplayingClose()
    {
        var fixture = new Fixture();
        fixture.Native.CloseResult = new(false, 6);
        using var acquired = So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native);
        acquired.Dispose();
        acquired.Dispose();
        Assert.Equal(So6120NativeCloseState.Failed, acquired.CloseEvidence.State);
        Assert.Equal(6, acquired.CloseEvidence.NativeError);
        Assert.Equal(1, fixture.Native.Calls.Count(call => call == "close"));
        Assert.DoesNotContain("release", fixture.Native.Calls);
        Assert.DoesNotContain("unlatch", fixture.Native.Calls);
    }

    [Fact]
    public void UnusableOpenedHandlePreservesExceptionalCloseAndOwnershipEvidence()
    {
        var fixture = new Fixture();
        fixture.Native.ReturnClosedHandle = true;
        fixture.Native.CloseResult = new(false, 6);
        var error = Assert.Throws<So6120NativeAcquisitionException>(() =>
            So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native));
        Assert.Equal(So6120NativeCloseState.Failed, error.CloseEvidence.State);
        Assert.Equal(6, error.CloseEvidence.NativeError);
        Assert.True(error.OwnershipUnresolved);
        Assert.DoesNotContain("release", fixture.Native.Calls);
    }

    [Fact]
    public void RunReservationRefusalStopsBeforeAnyMetadataOrDeviceOpen()
    {
        var fixture = new Fixture();
        fixture.Native.ReservationUnavailable = true;
        Assert.Throws<InvalidOperationException>(() => So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native));
        Assert.Equal(new[] { "reserve" }, fixture.Native.Calls);
    }

    [Fact]
    public void MachineAttemptLatchRemainsConsumedAfterItsHandleCloses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "openblade-so6120-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "attempt.json");
        Guid run = Guid.NewGuid();
        DateTimeOffset started = DateTimeOffset.UnixEpoch;
        try
        {
            using (So6120WindowsDirectDutyPlatform.CreateAttemptLatch(directory, run, started))
            {
                Assert.Throws<IOException>(() => So6120WindowsDirectDutyPlatform.CreateAttemptLatch(directory, Guid.NewGuid(), started));
            }
            Assert.Throws<IOException>(() => So6120WindowsDirectDutyPlatform.CreateAttemptLatch(directory, Guid.NewGuid(), started));
            using var record = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal(run, record.RootElement.GetProperty("runId").GetGuid());
            Assert.Equal(started, record.RootElement.GetProperty("startedUtc").GetDateTimeOffset());
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(directory)) { Directory.Delete(directory); }
        }
    }

    [Fact]
    public void EvidenceSourceReobservesScopeAndRestartingUserOwner()
    {
        var fixture = new Fixture();
        using var acquired = So6120WindowsDirectDutyPlatform.Acquire(fixture.Consent, fixture.Native);
        Assert.Equal(acquired.Binding.Identity, acquired.EvidenceSource.Capture().Identity);
        fixture.CurrentScope = fixture.CurrentScope with { PeripheralConfigurationSha256 = new('F', 64) };
        Assert.Throws<InvalidOperationException>(() => acquired.EvidenceSource.Capture());
        fixture.Native.UserOwnersStopped = false;
        Assert.Throws<InvalidOperationException>(() => acquired.EvidenceSource.Capture());
        Assert.Equal("owners", fixture.Native.Calls[^1]);
        Assert.Equal(1, fixture.Native.Calls.Count(call => call == "open"));
    }

    [Fact]
    public void PresentDeviceFingerprintIgnoresCaseAndOrderingButRejectsAmbiguousInventory()
    {
        const string first = @"HID\VID_1532&PID_02E0\ONE";
        const string second = @"USB\VID_1234&PID_5678\TWO";
        Assert.Equal(So6120WindowsDirectDutyPlatform.HashDeviceIds([first, second]),
            So6120WindowsDirectDutyPlatform.HashDeviceIds([second.ToLowerInvariant(), first.ToLowerInvariant()]));
        Assert.NotEqual(So6120WindowsDirectDutyPlatform.HashDeviceIds([first]),
            So6120WindowsDirectDutyPlatform.HashDeviceIds([first, second]));
        Assert.Throws<InvalidDataException>(() => So6120WindowsDirectDutyPlatform.HashDeviceIds([first, first.ToLowerInvariant()]));
    }

    private sealed class Fixture
    {
        public So6120AttendedBaselineConsent Consent { get; }
        public FakeNative Native { get; }
        public So6120ChallengeScope CurrentScope { get; set; }

        public Fixture(PerformancePreset profile = PerformancePreset.Silent)
        {
            var clock = new FixedClock();
            var owner = new DirectDutyOwner(Guid.NewGuid(), "fixture device", "fixture firmware", Guid.NewGuid());
            var identity = new So6120JournalIdentity(owner, profile, null, null,
                new('A', 64), new('B', 64), Guid.NewGuid(), new('C', 64), "fixture boot");
            var stamp = new So6120AdmissionStamp(owner.OperationId, 1, clock, clock.GetTimestamp());
            const string path = @"\\?\hid#vid_1532&pid_02e0&mi_03&col01#fixture";
            var driver = new So6120DriverMetadata(@"C:\Windows", @"\SystemRoot\System32\Drivers\inpoutx64.sys",
                @"C:\Windows\System32\Drivers\inpoutx64.sys", ServiceType.KernelDriver, ServiceControllerStatus.Running,
                15008, So6120SupervisedAdmission.DriverHash, @"\Device\inpoutx64");
            Native = new(new(stamp, stamp.Timestamp, Architecture.X64,
                new(new HostIdentityEvidence("Razer", "Blade 16 - RZ09-0581", "RZ09-05819EN4", "4.01"), null),
                new Version(1, 4), [new(path, new HidHandleIdentity(0x1532, 0x02E0, 1, 2, 91))], new(true, 1, 1, 1),
                driver, [], ServiceControllerStatus.Stopped, []));
            CurrentScope = new(path, "4.01", new Version(1, 4), driver.Sha256!, new('D', 64), new('E', 64));
            const So6120AcceptedResiduals residuals = So6120AcceptedResiduals.UnobservedWriters;
            var challenge = new So6120CallerChallengeContext(stamp, Guid.NewGuid(), new('C', 64), CurrentScope, CurrentScope, residuals, residuals);
            Consent = new(identity, 1, clock, challenge, _ => CurrentScope);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => TimeSpan.TicksPerSecond;
    }

    private sealed class FakeNative(So6120SupervisedMetadata metadata) : ISo6120WindowsDutyNative
    {
        public So6120SupervisedMetadata Metadata { get; set; } = metadata;
        public List<string> Calls { get; } = [];
        public bool UserOwnersStopped { get; set; } = true;
        public bool ReservationUnavailable { get; set; }
        public bool ReturnClosedHandle { get; set; }
        public int OpenError { get; set; }
        public So6120NativeCloseResult CloseResult { get; set; } = new(true, 0);
        public Action? AfterPin { get; set; }
        public Action? AfterOpen { get; set; }

        public IDisposable ReserveRun()
        {
            Calls.Add("reserve");
            if (ReservationUnavailable) { throw new InvalidOperationException("An SO6120 runner is already active."); }
            return new Resource(Calls, "release");
        }

        public IDisposable CreateAttemptLatch(Guid runId, DateTimeOffset startedUtc)
        { Calls.Add("latch"); return new Resource(Calls, "unlatch"); }

        public So6120MetadataRead Capture(Guid runId, TimeProvider clock)
        { Calls.Add("metadata"); return new(Metadata, null, null); }

        public void VerifyUserOwnersStopped()
        {
            Calls.Add("owners");
            if (!UserOwnersStopped) { throw new InvalidOperationException("An installed user owner is active."); }
        }

        public IDisposable PinDriverFile(So6120DriverMetadata driver)
        {
            Calls.Add("pin");
            AfterPin?.Invoke();
            return new Resource(Calls, "unpin");
        }

        public So6120OwnedInpOutHandle OpenDevice()
        {
            Calls.Add("open");
            if (OpenError != 0) { throw new Win32Exception(OpenError); }
            AfterOpen?.Invoke();
            var handle = new So6120OwnedInpOutHandle(1234, _ => { Calls.Add("close"); return CloseResult; });
            if (ReturnClosedHandle) { handle.Dispose(); }
            return handle;
        }

        private sealed class Resource(List<string> calls, string released) : IDisposable
        {
            public void Dispose() => calls.Add(released);
        }
    }
}
