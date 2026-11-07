using System.Runtime.InteropServices;
using System.ServiceProcess;
using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class So6120DirectDutySessionTests
{
    [Theory]
    [InlineData(PerformancePreset.Silent, 5)]
    [InlineData(PerformancePreset.Performance, 2)]
    [InlineData(PerformancePreset.BatteryBalanced, 6)]
    public void ActualCompositionEntersAndExplicitlyRestoresCapturedAutomaticProfile(PerformancePreset profile, byte selector)
    {
        using var f = new Fixture(profile, selector);
        using (var session = f.Create())
        {
            var entry = session.Enter();
            Assert.True(entry.Success);
            Assert.True(entry.PermissionExposureMayRemain);
            Assert.Equal(new byte[] { 0xB8, 0xB9, 0xF0, 0x1A }, f.Port.Writes.Select(x => x.Register));
            Assert.All(f.Port.Publications, p => Assert.True(p.DurableCapture >= p.Capture));
            Assert.All(f.Port.Publications.Zip(f.Port.Publications.Skip(1)), pair => Assert.True(pair.Second.Capture >= pair.First.Capture + 2));
            Assert.All(entry.Observations, o => Assert.True(o.CompletedTimestamp >= o.StartedTimestamp));
            Assert.Equal(13, f.Port.Publications.TakeWhile(p => p.Command == 0xA0).Take(13).Count());
            var exit = session.Restore();
            Assert.True(exit.Success);
            Assert.True(exit.CleanupConfirmed);
            Assert.True(exit.PermissionRestoredConfirmed);
            Assert.Equal(selector, f.Port.Mapped[0x1A]);
            Assert.Equal((byte)0x11, f.Port.Mapped[0xF0]);
            Assert.Equal((byte)10, f.Port.Mapped[0xB8]);
            Assert.Equal((byte)20, f.Port.Mapped[0xB9]);
            Assert.DoesNotContain(f.Port.Writes, w => w.Register is 0x36 or 0x0A);
            Assert.Throws<InvalidOperationException>(() => session.Restore());
            Assert.Throws<InvalidOperationException>(() => session.Enter());
        }
        var inspection = So6120DirectDutyJournal.Inspect(f.Root);
        Assert.True(inspection.Complete);
        Assert.False(inspection.CanResume);
        Assert.False(inspection.Corrupt);
        Assert.InRange(inspection.Records, 1, So6120DirectDutyJournal.MaximumRecords);
        Assert.InRange(new FileInfo(Path.Combine(f.Root, So6120DirectDutyJournal.FileName)).Length, 1, So6120DirectDutyJournal.MaximumFileBytes);
        Assert.True(f.Port.Disposed);
    }

    [Fact]
    public void ChallengeAndConsentFieldsCanHaveDifferentValues()
    {
        using var f = new Fixture();
        Assert.NotEqual(f.Identity.ConsentId, f.Binding.Challenge.DecisionId);
        Assert.NotEqual(f.Identity.ConsentSha256, f.Binding.Challenge.EvidenceSha256);
        using var session = f.Create();
        Assert.True(session.Enter().Success);
        Assert.True(session.Restore().Success);
    }

    [Fact]
    public void ExplicitAdmissionPolicyFailureFencesBeforeAnyPortOperation()
    {
        using var fixture = new Fixture();
        var policy = new RefusingAdmissionPolicy();
        using var session = So6120DirectDutySession.CreateInjected(fixture.Root, fixture.Binding,
            fixture.Port, fixture.Source, fixture.Clock, admissionPolicy: policy);

        var result = session.Enter();

        Assert.True(result.CommandsFenced);
        Assert.Equal(1, policy.Calls);
        Assert.Empty(fixture.Port.Publications);
        Assert.Empty(fixture.Port.Writes);
        Assert.Throws<InvalidOperationException>(() => session.Restore());
    }

    private sealed class RefusingAdmissionPolicy : ISo6120SessionAdmissionPolicy
    {
        internal int Calls { get; private set; }

        public So6120AdmissionAssessment Assess(TimeProvider clock, So6120SupervisedMetadata metadata,
            So6120CallerChallengeContext challenge, So6120OpenBladeHandoff handoff)
        {
            Calls++;
            return So6120AdmissionAssessment.ControllerDetected;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void SwappedOrStaleEvidenceCannotPublish(int change)
    {
        using var f = new Fixture();
        f.Source.Modify = e => change switch
        {
            0 => e with { Identity = e.Identity with { BootIdentity = "different boot" } },
            1 => e with { Identity = e.Identity with { ConsentId = Guid.NewGuid() } },
            2 => e with { Challenge = e.Challenge with { DecisionId = Guid.NewGuid() } },
            3 => SwapContext(e, Guid.NewGuid(), 1),
            4 => e with { Metadata = e.Metadata with { StartedTimestamp = e.Metadata.StartedTimestamp - 1 } },
            5 => SwapContext(e, e.Metadata.Stamp.RunId, 2),
            6 => e with { Identity = e.Identity with { ConsentSha256 = new('0', 64) } },
            _ => e with { Challenge = e.Challenge with { EvidenceSha256 = new('0', 64) } },
        };
        using var session = f.Create();
        var result = session.Enter();
        Assert.True(result.CommandsFenced);
        Assert.Empty(f.Port.Publications);
        Assert.Throws<InvalidOperationException>(() => session.Enter());
    }

    private static So6120SessionEvidence SwapContext(So6120SessionEvidence evidence, Guid run, uint console) => evidence with
    {
        Metadata = evidence.Metadata with
        {
            Stamp = evidence.Metadata.Stamp with { RunId = run, ConsoleSession = console },
            Session = evidence.Metadata.Session with { ConsoleSession = console, ProcessSession = (int)console },
        },
        Challenge = evidence.Challenge with { Stamp = evidence.Challenge.Stamp with { RunId = run, ConsoleSession = console } },
        Handoff = evidence.Handoff with { Stamp = evidence.Handoff.Stamp with { RunId = run, ConsoleSession = console } },
    };

    [Fact]
    public void LossOfAdmissionAtConfirmationPrepareStopsAllCommands()
    {
        using var f = new Fixture();
        f.Port.AfterWrite = (_, _) => f.Source.Deny = true;
        using (var session = f.Create())
        {
            var result = session.Enter();
            Assert.True(result.CommandsFenced);
            Assert.Single(f.Port.Writes);
            var transaction = Assert.Single(result.Writes).Transaction;
            Assert.NotNull(transaction);
            Assert.True(transaction.WriteHandlerCompleted);
            Assert.Equal(1, transaction.CommandsPossiblySubmitted);
            Assert.Equal(1, transaction.CommandsCompleted);
            Assert.Contains(transaction.Operations, x => x.Phase == "publish" && x.Argument == 0xA1);
            Assert.True(result.DurableFenceConfirmed);
            Assert.Equal(0xA1, f.Port.Publications[^1].Command);
            int count = f.Port.Calls;
            Assert.Throws<InvalidOperationException>(() => session.Restore());
            Assert.Equal(count, f.Port.Calls);
        }
        Assert.True(So6120DirectDutyJournal.Inspect(f.Root).Fenced);
    }

    [Fact]
    public void AmbiguousCommandErrorNeverTriggersCleanupOrConfirmation()
    {
        using var f = new Fixture();
        f.Port.AmbiguousFirstWrite = true;
        using (var session = f.Create())
        {
            var result = session.Enter();
            Assert.True(result.CommandsFenced);
            Assert.Single(f.Port.Writes);
            Assert.Equal(0xA1, f.Port.Publications[^1].Command);
            Assert.Throws<InvalidOperationException>(() => session.Restore());
        }
        Assert.Single(f.Port.Writes);
    }

    [Fact]
    public void ExplicitlyUnperformedSecondWriteAllowsOnlyExplicitPartialExit()
    {
        using var f = new Fixture();
        f.Port.UnperformedSecondWrite = true;
        using (var session = f.Create())
        {
            var result = session.Enter();
            Assert.Equal(So6120DutyFailure.NotSubmitted, result.Failure);
            Assert.False(result.CommandsFenced);
            Assert.Single(f.Port.Writes);
            Assert.True(session.Restore().Success);
            Assert.Equal(new byte[] { 30, 10 }, f.Port.Writes.Select(x => x.Value));
        }
        Assert.True(So6120DirectDutyJournal.Inspect(f.Root).Complete);
    }

    [Fact]
    public void JournalFailureAfterStagingButBeforePublicationPreventsCommand()
    {
        using var f = new Fixture();
        bool armed = false;
        using (var session = f.Create(stage =>
        {
            if (armed && stage == So6120JournalWriteStage.BeforeFlush) { throw new IOException("synthetic flush failure"); }
        }))
        {
            f.Port.BeforeFirstWritePublication = () => armed = true;
            var result = session.Enter();
            Assert.True(result.CommandsFenced);
            Assert.False(result.DurableFenceConfirmed);
            Assert.NotNull(Assert.Single(result.Writes).Transaction);
            Assert.Empty(f.Port.Writes);
            Assert.DoesNotContain(f.Port.Publications, p => p.Command == 0xA1);
            Assert.Throws<InvalidOperationException>(() => session.Restore());
        }
    }

    [Fact]
    public void PoisonedJournalStillReturnsThePublishedA1LedgerWithoutConfirmationOrCleanup()
    {
        using var f = new Fixture();
        bool armed = false;
        using var session = f.Create(stage =>
        {
            if (armed && stage == So6120JournalWriteStage.BeforeFlush) { throw new IOException("synthetic flush failure"); }
        });
        f.Port.AfterWrite = (_, _) => armed = true;
        var result = session.Enter();
        Assert.True(result.CommandsFenced);
        Assert.False(result.DurableFenceConfirmed);
        var transaction = Assert.Single(result.Writes).Transaction;
        Assert.NotNull(transaction);
        Assert.True(transaction.WriteHandlerCompleted);
        Assert.Equal(1, transaction.CommandsPossiblySubmitted);
        Assert.Equal(1, transaction.CommandsCompleted);
        Assert.Contains(transaction.Operations, x => x.Phase == "publish" && x.Argument == 0xA1);
        Assert.Equal(0xA1, f.Port.Publications[^1].Command);
        Assert.Throws<InvalidOperationException>(() => session.Restore());
    }

    [Fact]
    public void TransactionDeadlineAfterKnownPublicationIsUnresolved()
    {
        using var f = new Fixture();
        f.Port.AfterWrite = (_, _) => f.Clock.Advance(TimeSpan.FromSeconds(3));
        using var session = f.Create();
        var result = session.Enter();
        Assert.True(result.CommandsFenced);
        Assert.Single(f.Port.Writes);
        Assert.Equal(0xA1, f.Port.Publications[^1].Command);
    }

    [Fact]
    public void StaleAdmissionAfterSlowFlushDoesNotPublish()
    {
        using var f = new Fixture();
        bool delayed = false;
        using var session = f.Create(stage =>
        {
            if (!delayed && f.Source.Calls == 3 && stage == So6120JournalWriteStage.AfterFlush)
            { delayed = true; f.Clock.Advance(TimeSpan.FromSeconds(6)); }
        });
        var result = session.Enter();
        Assert.True(result.CommandsFenced);
        Assert.Empty(f.Port.Publications);
    }

    [Fact]
    public void SnapshotDeadlineStopsBetweenResolvedReadsWithoutInventingSnapshot()
    {
        using var f = new Fixture();
        f.Port.AfterPublication = () => f.Clock.Advance(TimeSpan.FromMilliseconds(40));
        using var session = f.Create(options: new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250)));
        var result = session.Enter();
        Assert.False(result.CommandsFenced);
        Assert.Equal(So6120DutyFailure.NotSubmitted, result.Failure);
        Assert.Empty(result.Observations);
        Assert.InRange(f.Port.Publications.Count, 1, 12);
        Assert.Empty(f.Port.Writes);
    }

    [Fact]
    public void DisposalDoesNotRestoreAndOneShotPathCannotBeReused()
    {
        using var f = new Fixture();
        var session = f.Create();
        Assert.True(session.Enter().Success);
        int calls = f.Port.Calls;
        session.Dispose();
        session.Dispose();
        Assert.Equal(calls, f.Port.Calls);
        Assert.Equal((byte)0xFE, f.Port.Mapped[0x1A]);
        var inspection = So6120DirectDutyJournal.Inspect(f.Root);
        Assert.False(inspection.Complete);
        Assert.True(inspection.UnresolvedWork);
        Assert.True(inspection.PermissionPublicationPossible);
        Assert.False(inspection.PermissionRestoredConfirmed);
        Assert.Throws<ObjectDisposedException>(() => session.Restore());
        Assert.Throws<IOException>(() => f.Create());
    }

    [Fact]
    public void ReentrantSourceCannotStartAnotherWorkflow()
    {
        using var f = new Fixture();
        using var session = f.Create();
        f.Source.OnCapture = () => Assert.Throws<InvalidOperationException>(() => session.Enter());
        Assert.True(session.Enter().Success);
        Assert.True(session.Restore().Success);
    }

    [Fact]
    public void AcquireRemainsUnavailable()
    {
        Assert.Throws<NotSupportedException>(() => So6120DirectDutySession.Acquire());
    }

    [Fact]
    public void MaximumDefaultPwmObservationAttemptStillLeavesRoomForExplicitHandback()
    {
        using var f = new Fixture();
        f.Port.UpdatePwm = false;
        using (var session = f.Create())
        {
            var entry = session.Enter();
            Assert.Equal(So6120DutyFailure.PwmNotObserved, entry.Failure);
            Assert.False(entry.CommandsFenced);
            Assert.True(session.Restore().Success);
        }
        var inspection = So6120DirectDutyJournal.Inspect(f.Root);
        Assert.True(inspection.Complete);
        Assert.InRange(inspection.Records, 1, So6120DirectDutyJournal.MaximumRecords);
        Assert.InRange(new FileInfo(Path.Combine(f.Root, So6120DirectDutyJournal.FileName)).Length, 1, So6120DirectDutyJournal.MaximumFileBytes);
    }

    [Fact]
    public async Task DisposalWaitsForTheSerializedInvocationAndNeverRunsCleanup()
    {
        using var f = new Fixture();
        using var paused = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposeAttempted = new ManualResetEventSlim();
        using var session = f.Create();
        f.Source.OnCapture = () =>
        {
            if (f.Source.Calls != 1) { return; }
            paused.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var entry = Task.Run(() => session.Enter());
        Assert.True(paused.Wait(TimeSpan.FromSeconds(10)));
        var dispose = Task.Run(() => { disposeAttempted.Set(); session.Dispose(); });
        try
        {
            Assert.True(disposeAttempted.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(f.Port.Disposed);
            Assert.Empty(f.Port.Publications);
        }
        finally { release.Set(); }
        Assert.True((await entry).Success);
        await dispose;
        Assert.True(f.Port.Disposed);
        Assert.Equal(4, f.Port.Writes.Count);
        Assert.False(So6120DirectDutyJournal.Inspect(f.Root).Complete);
    }

    internal sealed class Clock : TimeProvider
    {
        private long ticks = TimeSpan.FromHours(1).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
    internal sealed class EvidenceSource(So6120SessionBinding binding, Clock clock) : ISo6120SessionEvidenceSource
    {
        public int Calls { get; private set; }
        public bool Deny { get; set; }
        public Func<So6120SessionEvidence, So6120SessionEvidence>? Modify { get; set; }
        public Action? OnCapture { get; set; }
        public So6120SessionEvidence Capture()
        {
            Calls++;
            OnCapture?.Invoke();
            long start = clock.GetTimestamp();
            var stamp = new So6120AdmissionStamp(binding.Identity.Owner.OperationId, 1, clock, start);
            var metadata = new So6120SupervisedMetadata(stamp, start, Architecture.X64,
                new(new HostIdentityEvidence("Razer", "Blade 16 - RZ09-0581", "RZ09-05819EN4", "4.01"), null),
                new(1, 4), [new(binding.Challenge.ObservedScope.HidPath, new(0x1532, 0x02E0, 1, 2, 91))],
                new(true, 1, 1, Deny ? (byte)0 : (byte)1),
                new(@"C:\Windows", @"\SystemRoot\System32\Drivers\inpoutx64.sys", @"C:\Windows\System32\Drivers\inpoutx64.sys",
                    ServiceType.KernelDriver, ServiceControllerStatus.Running, 15008, So6120SupervisedAdmission.DriverHash, @"\Device\inpoutx64"),
                [], ServiceControllerStatus.Stopped, []);
            var evidence = new So6120SessionEvidence(binding.Identity, metadata, binding.Challenge, binding.Handoff);
            return Modify?.Invoke(evidence) ?? evidence;
        }
    }
    internal sealed class MemoryPort : ISo6120MailboxBytePort, IDisposable
    {
        public byte[] Mapped { get; } = new byte[256];
        private readonly byte[] mailbox = new byte[256];
        private byte selected;
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public bool AmbiguousFirstWrite { get; set; }
        public bool UnperformedSecondWrite { get; set; }
        public bool UpdatePwm { get; set; } = true;
        public bool FailNextReadUnperformed { get; set; }
        public Action<byte, byte>? AfterWrite { get; set; }
        public Action? BeforeFirstWritePublication { get; set; }
        public Action? AfterPublication { get; set; }
        public Func<(int Capture, int Durable)> Captures { get; set; } = () => (0, 0);
        public List<(byte Register, byte Value)> Writes { get; } = [];
        public List<(byte Command, byte Register, int Capture, int DurableCapture)> Publications { get; } = [];
        public So6120PortResult SelectIndex(byte index) { Calls++; selected = index; return Done(); }
        public So6120PortResult ReadData()
        {
            Calls++;
            if (FailNextReadUnperformed) { FailNextReadUnperformed = false; return new(false, 0, 0, 5, So6120PortEffect.NotPerformed); }
            return new(true, 1, mailbox[selected], 0, So6120PortEffect.Performed);
        }
        public So6120PortResult WriteData(byte value)
        {
            Calls++;
            mailbox[selected] = value;
            if (selected == 0xA0 && Writes.Count == 0) { BeforeFirstWritePublication?.Invoke(); }
            if (selected == 0x93)
            {
                var captures = Captures();
                Publications.Add((value, mailbox[0x91], captures.Capture, captures.Durable));
                if (value == 0xA1)
                {
                    if (UnperformedSecondWrite && mailbox[0x91] == 0xB9)
                    { mailbox[0x93] = 0; return new(false, 0, 0, 5, So6120PortEffect.NotPerformed); }
                    byte register = mailbox[0x91], data = mailbox[0xA0];
                    Mapped[register] = data;
                    Writes.Add((register, data));
                    if (UpdatePwm && register == 0x1A && data == 0xFE)
                    { Mapped[0xB0] = Mapped[0xB8]; Mapped[0xB1] = Mapped[0xB9]; }
                    AfterWrite?.Invoke(register, data);
                    if (AmbiguousFirstWrite) { return new(false, 0, 0, 5, So6120PortEffect.MayHavePerformed); }
                }
                else { mailbox[0xA0] = Mapped[mailbox[0x91]]; }
                mailbox[0x94] = 0x80;
                mailbox[0x93] = 0;
                AfterPublication?.Invoke();
            }
            return Done();
        }
        private static So6120PortResult Done() => new(true, 10, 0, 0, So6120PortEffect.Performed);
        public void Dispose() => Disposed = true;
    }
    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ob-session-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public MemoryPort Port { get; } = new();
        public So6120JournalIdentity Identity { get; }
        public So6120SessionBinding Binding { get; }
        public EvidenceSource Source { get; }
        private int durableCapture;
        public Fixture(PerformancePreset profile = PerformancePreset.Silent, byte selector = 5, bool preparing = false,
            bool zeroDuty = false)
        {
            Identity = new(new(Guid.NewGuid(), "synthetic SO6120", "synthetic EC1.04", Guid.NewGuid()), profile,
                preparing ? null : zeroDuty ? (byte)0 : (byte)30, preparing ? null : zeroDuty ? (byte)0 : (byte)40,
                new('A', 64), new('B', 64), Guid.NewGuid(), new('D', 64), "synthetic-boot");
            var stamp = new So6120AdmissionStamp(Identity.Owner.OperationId, 1, Clock, Clock.GetTimestamp());
            var scope = new So6120ChallengeScope(@"\\?\hid#vid_1532&pid_02e0&mi_03&col01#synthetic", "4.01", new(1, 4),
                So6120SupervisedAdmission.DriverHash, new('E', 64), new('F', 64));
            var residuals = So6120AcceptedResiduals.UnnamedFileHandles | So6120AcceptedResiduals.UncoveredAcpiPaths
                | So6120AcceptedResiduals.UnobservedWriters;
            Binding = new(Identity, 1, new(stamp, Guid.NewGuid(), new('C', 64), scope, scope, residuals, residuals),
                new(stamp, So6120HandoffOrigin.AlreadyStopped, null));
            Source = new(Binding, Clock);
            Port.Captures = () => (Source.Calls, durableCapture);
            Port.Mapped[0x1A] = selector; Port.Mapped[0xF0] = 0x11;
            Port.Mapped[0xB8] = 10; Port.Mapped[0xB9] = 20; Port.Mapped[0xB2] = 183;
            Port.Mapped[0xB0] = 50; Port.Mapped[0xB1] = 60; Port.Mapped[0x06] = 1;
        }
        public So6120DirectDutySession Create(Action<So6120JournalWriteStage>? fault = null, So6120SessionOptions? options = null) =>
            So6120DirectDutySession.CreateInjected(Root, Binding, Port, Source, Clock, options, stage =>
            {
                fault?.Invoke(stage);
                if (stage == So6120JournalWriteStage.AfterFlush) { durableCapture = Source.Calls; }
            });
        public void Dispose()
        {
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }
}
