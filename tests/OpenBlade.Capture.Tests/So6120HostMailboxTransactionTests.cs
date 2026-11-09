using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class So6120HostMailboxTransactionTests
{
    [Fact]
    public void ReadUsesBankZeroOneByteAndFreshStatusBeforeCommandLast()
    {
        var fake = new Fake();
        fake.Registers[0xB0] = 43;
        fake.Mailbox[0x94] = 0x80;
        var result = Core(fake).Read(So6120MailboxRegister.ActualDuty1);
        Assert.True(result.Success);
        Assert.Equal((byte)43, result.Value);
        Assert.Equal(1, result.CommandsCompleted);
        Assert.False(result.WriteConfirmed);
        Assert.Equal(new (byte, byte)[] { (0x94, 0), (0x90, 0), (0x91, 0xB0), (0x92, 1), (0x93, 0xA0) }, fake.Writes);
        Assert.Equal(new[] { "prepare", "admit", "publish" }, fake.Events);
        Assert.All(result.Operations, item => Assert.NotNull(item.Result));
    }

    [Fact]
    public void WriteRequiresDistinctFreshReadAndAcceptsNativeWriteInformationTen()
    {
        var fake = new Fake();
        var result = Core(fake).WriteAndConfirm(So6120MailboxRegister.StagedDuty1, 33);
        Assert.True(result.WriteConfirmed);
        Assert.Equal((byte)33, result.Value);
        Assert.Equal(2, result.CommandsCompleted);
        Assert.Equal(2, result.CommandsPossiblySubmitted);
        Assert.Equal(new byte[] { 0xA1, 0xA0 }, fake.Commands);
        Assert.Equal(2, fake.Prepared.Select(item => item.Id).Distinct().Count());
        Assert.Equal(new byte?[] { 33, null }, fake.Prepared.Select(item => item.WriteValue));
        Assert.Equal(2, fake.Writes.Count(item => item == ((byte)0x94, (byte)0)));
        Assert.Equal(new[] { "prepare", "admit", "publish", "prepare", "admit", "publish" }, fake.Events);
        Assert.All(result.Operations.Where(item => item.Operation != So6120PortOperation.ReadData),
            item => Assert.Equal(10u, item.Result!.BytesReturned));
    }

    [Fact]
    public void ThirdStagingFieldDoesNotAcquireLiveMailboxAuthority()
    {
        var fake = new Fake();
        Assert.Equal(So6120MailboxFailure.InvalidRequest, Core(fake).Read(So6120MailboxRegister.StagedDuty3).Failure);
        Assert.Equal(So6120MailboxFailure.InvalidRequest, Core(fake).WriteAndConfirm(So6120MailboxRegister.StagedDuty3, 0).Failure);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(fake.Prepared);
    }

    [Fact]
    public void SuccessfulHandlerButSkippedWriteDoesNotConfirmPayloadCache()
    {
        var fake = new Fake { SkipMappedWrite = true };
        fake.Registers[0xB8] = 7;
        var result = Core(fake).WriteAndConfirm(So6120MailboxRegister.StagedDuty1, 33);
        Assert.Equal(So6120MailboxFailure.ConfirmationMismatch, result.Failure);
        Assert.True(result.WriteHandlerCompleted);
        Assert.False(result.WriteConfirmed);
        Assert.Null(result.Value);
        Assert.Equal(2, result.CommandsCompleted);
    }

    [Theory]
    [InlineData(0x0F)]
    [InlineData(0xFF)]
    [InlineData(0x00)]
    public void UnknownRegisterNeverTouchesBoundary(int register)
    {
        var fake = new Fake();
        Assert.Equal(So6120MailboxFailure.InvalidRequest, Core(fake).Read((So6120MailboxRegister)register).Failure);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(fake.Prepared);
    }

    [Theory]
    [InlineData(0xE3)]
    [InlineData(0xE4)]
    public void FamilyIdentityReadNeedsSeparateAuthorityAndNeverPermitsWrites(int register)
    {
        var refused = new Fake();
        Assert.Equal(So6120MailboxFailure.InvalidRequest, Core(refused).Read((So6120MailboxRegister)register).Failure);
        Assert.Equal(0, refused.Calls);
        var admitted = new Fake();
        admitted.Registers[register] = 0x30;
        var read = new So6120HostMailboxTransaction(admitted, admitted, allowBoardIdentityReads: true)
            .Read((So6120MailboxRegister)register);
        Assert.True(read.Success);
        Assert.Equal((byte)0x30, read.Value);
        Assert.Equal(new byte[] { 0xA0 }, admitted.Commands);
        var write = new Fake();
        Assert.Equal(So6120MailboxFailure.InvalidRequest,
            new So6120HostMailboxTransaction(write, write, allowBoardIdentityReads: true)
                .WriteAndConfirm((So6120MailboxRegister)register, 0).Failure);
        Assert.Equal(0, write.Calls);
    }

    [Theory]
    [InlineData(0x06)]
    [InlineData(0x08)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0B)]
    [InlineData(0xB0)]
    [InlineData(0xB1)]
    [InlineData(0xB2)]
    public void ReadOnlyRegistersCannotBeWritten(int register)
    {
        var fake = new Fake();
        Assert.Equal(So6120MailboxFailure.InvalidRequest,
            Core(fake).WriteAndConfirm((So6120MailboxRegister)register, 0).Failure);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void BusyMailboxIsNeverClearedOrOverwritten()
    {
        var fake = new Fake();
        fake.Mailbox[0x93] = 0xD0;
        var result = Core(fake).Read(So6120MailboxRegister.Selector);
        Assert.Equal(So6120MailboxFailure.Busy, result.Failure);
        Assert.Empty(fake.Writes);
        Assert.Equal(0, result.CommandsPossiblySubmitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionRefusalNeverPublishes(bool atPublication)
    {
        var fake = new Fake { RefusePreparation = !atPublication, RefusePublication = atPublication };
        var result = Core(fake).Read(So6120MailboxRegister.Selector);
        Assert.Equal(So6120MailboxFailure.AdmissionRefused, result.Failure);
        Assert.Empty(fake.Commands);
        if (!atPublication) { Assert.Equal(0, fake.Calls); }
    }

    [Theory]
    [InlineData(0x90, (int)So6120MailboxFailure.DescriptorChanged)]
    [InlineData(0x91, (int)So6120MailboxFailure.DescriptorChanged)]
    [InlineData(0x92, (int)So6120MailboxFailure.DescriptorChanged)]
    [InlineData(0x94, (int)So6120MailboxFailure.StatusRejected)]
    [InlineData(0x93, (int)So6120MailboxFailure.Busy)]
    [InlineData(0xA0, (int)So6120MailboxFailure.DescriptorChanged)]
    public void AdmissionCallbackMutationCannotPublish(int field, int expected)
    {
        var fake = new Fake();
        fake.AfterAdmission = () => fake.Mailbox[field]++;
        var result = Core(fake).WriteAndConfirm(So6120MailboxRegister.StagedDuty1, 33);
        Assert.Equal((So6120MailboxFailure)expected, result.Failure);
        Assert.Equal(0, result.CommandsPossiblySubmitted);
        Assert.Equal(0, result.CommandsCompleted);
        Assert.Empty(fake.Commands);
        Assert.DoesNotContain(fake.Writes, item => item.Index == 0x93);
        Assert.Equal("post-admission", result.Operations[^1].Phase);
    }

    [Theory]
    [InlineData(0x90)]
    [InlineData(0x91)]
    [InlineData(0x92)]
    [InlineData(0x94)]
    [InlineData(0x93)]
    public void ConfirmationAdmissionMutationRetainsOnlyTheOriginalPublication(int field)
    {
        var fake = new Fake();
        fake.AfterAdmission = () =>
        {
            if (fake.Prepared.Count == 2) { fake.Mailbox[field]++; }
        };
        var result = Core(fake).WriteAndConfirm(So6120MailboxRegister.StagedDuty1, 33);
        Assert.False(result.Success);
        Assert.True(result.WriteHandlerCompleted);
        Assert.Equal(1, result.CommandsPossiblySubmitted);
        Assert.Equal(1, result.CommandsCompleted);
        Assert.Equal((byte)0xA1, Assert.Single(fake.Commands));
        Assert.Single(fake.Writes, item => item.Index == 0x93);
        Assert.Equal("post-admission", result.Operations[^1].Phase);
    }

    [Fact]
    public void EveryFailedPortOperationStopsImmediatelyAndRetainsItsResult()
    {
        var baseline = new Fake();
        int operations = Core(baseline).WriteAndConfirm(So6120MailboxRegister.StagedDuty1, 33).Operations.Count;
        for (int fail = 1; fail <= operations; fail++)
        {
            var fake = new Fake { FailAt = fail };
            var result = Core(fake).WriteAndConfirm(So6120MailboxRegister.StagedDuty1, 33);
            Assert.Equal(So6120MailboxFailure.PortFailure, result.Failure);
            Assert.Equal(fail, fake.Calls);
            Assert.Equal(fail, result.Operations.Count);
            Assert.Equal(1234, result.Operations[^1].Result!.NativeError);
            Assert.False(result.Success);
            Assert.True(result.RequiresReadOnlyReconciliation);
        }
    }

    [Fact]
    public void EveryThrownPortOperationIsRetainedAndCannotTriggerCleanupOrRetry()
    {
        var baseline = new Fake();
        int operations = Core(baseline).WriteAndConfirm(So6120MailboxRegister.Manual, 1).Operations.Count;
        for (int fail = 1; fail <= operations; fail++)
        {
            var fake = new Fake { ThrowAt = fail };
            var core = Core(fake);
            var result = core.WriteAndConfirm(So6120MailboxRegister.Manual, 1);
            Assert.Equal(So6120MailboxFailure.PortFailure, result.Failure);
            Assert.Equal(fail, fake.Calls);
            Assert.Equal(typeof(IOException).FullName, result.Operations[^1].ExceptionType);
            Assert.Null(result.Operations[^1].Result);
            Assert.Equal(So6120MailboxFailure.AlreadyUsed, core.Read(So6120MailboxRegister.Manual).Failure);
            Assert.Equal(fail, fake.Calls);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public void ReadRequiresExactlyOneReturnedByte(uint count)
    {
        var fake = new Fake { ReadCount = count };
        var result = Core(fake).Read(So6120MailboxRegister.Selector);
        Assert.Equal(So6120MailboxFailure.PortFailure, result.Failure);
        Assert.Equal(count, result.Operations[^1].Result!.BytesReturned);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public void LostPublicationResultIsPossibleSubmissionEvenWhenDriverReportsFailure()
    {
        var fake = new Fake { LosePublishResult = true };
        var result = Core(fake).WriteAndConfirm(So6120MailboxRegister.Manual, 1);
        Assert.Equal(So6120MailboxFailure.PortFailure, result.Failure);
        Assert.Equal(1, result.CommandsPossiblySubmitted);
        Assert.Single(fake.Commands);
        Assert.Equal((byte)1, fake.Registers[0x36]);
        Assert.False(result.WriteHandlerCompleted);
        Assert.Equal("publish", result.Operations[^1].Phase);
    }

    [Fact]
    public void KnownUnperformedPublicationIsDistinctFromPossibleSubmission()
    {
        var baseline = new Fake();
        var success = Core(baseline).Read(So6120MailboxRegister.Manual);
        int publish = success.Operations.Select((item, i) => (item, i))
            .Single(pair => pair.item.Phase == "publish" && pair.item.Operation == So6120PortOperation.WriteData).i + 1;
        var fake = new Fake { FailAt = publish, FailureEffect = So6120PortEffect.NotPerformed };
        var result = Core(fake).Read(So6120MailboxRegister.Manual);
        Assert.Equal(So6120MailboxFailure.PortFailure, result.Failure);
        Assert.Equal(0, result.CommandsPossiblySubmitted);
        Assert.Empty(fake.Commands);
    }

    [Theory]
    [InlineData(0x90)]
    [InlineData(0x91)]
    [InlineData(0x92)]
    public void ChangedDescriptorAfterCompletionCannotSucceed(int field)
    {
        var fake = new Fake { CorruptDescriptor = (byte)field };
        var result = Core(fake).Read(So6120MailboxRegister.Selector);
        Assert.Equal(So6120MailboxFailure.DescriptorChanged, result.Failure);
        Assert.Null(result.Value);
        Assert.Equal(0, result.CommandsCompleted);
        Assert.Single(fake.Commands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x82)]
    [InlineData(0x84)]
    public void CommandClearRequiresFreshSuccessStatus(int status)
    {
        var fake = new Fake { CompletionStatus = (byte)status };
        fake.Mailbox[0x94] = 0x80;
        var result = Core(fake).Read(So6120MailboxRegister.Selector);
        Assert.Equal(So6120MailboxFailure.StatusRejected, result.Failure);
        Assert.Null(result.Value);
        Assert.Single(fake.Commands);
    }

    [Fact]
    public void DelayedConfirmationCanCompleteWithinTheOriginalBudget()
    {
        var clock = new Clock();
        var fake = new Fake();
        fake.AfterPublish = () =>
        {
            if (fake.Commands[^1] == 0xA0) { fake.Mailbox[0x93] = 0xA0; }
        };
        // A rapid 128-poll burst exhausted in under 247 ms on the attended run.
        // Model a separate confirmation taking longer, with no new publication.
        var result = Core(fake, clock, timeout: TimeSpan.FromSeconds(2), afterWait: () =>
        {
            if (clock.GetElapsedTime(0) >= TimeSpan.FromMilliseconds(300)) { fake.Mailbox[0x93] = 0; }
        }).WriteAndConfirm(So6120MailboxRegister.StagedDuty2, 64);
        Assert.True(result.WriteConfirmed);
        Assert.Equal((byte)64, result.Value);
        Assert.Equal(2, result.CommandsCompleted);
        Assert.Equal(new byte[] { 0xA1, 0xA0 }, fake.Commands);
        Assert.InRange(clock.GetElapsedTime(0), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1917)]
    public void PendingConfirmationStopsAtTheSharedDeadline(int writeElapsedMilliseconds)
    {
        var clock = new Clock();
        var fake = new Fake();
        fake.AfterPublish = () =>
        {
            if (fake.Commands[^1] == 0xA1) { clock.Ticks = TimeSpan.FromMilliseconds(writeElapsedMilliseconds).Ticks; }
            else { fake.Mailbox[0x93] = 0xA0; }
        };
        var result = Core(fake, clock, timeout: TimeSpan.FromSeconds(2))
            .WriteAndConfirm(So6120MailboxRegister.StagedDuty2, 64);
        Assert.Equal(So6120MailboxFailure.Deadline, result.Failure);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetElapsedTime(0));
        Assert.True(result.WriteHandlerCompleted);
        Assert.Equal(1, result.CommandsCompleted);
        Assert.False(result.WriteConfirmed);
        Assert.Equal(new byte[] { 0xA1, 0xA0 }, fake.Commands);
        Assert.Equal((byte)0xA0, fake.Mailbox[0x93]);
        Assert.Equal("poll", result.Operations[^1].Phase);
        Assert.InRange(result.Operations.Count(item => item.Phase == "poll"
            && item.Operation == So6120PortOperation.ReadData), 2, 129);
    }

    [Theory]
    [InlineData((int)So6120MailboxFailure.CommandChanged)]
    [InlineData((int)So6120MailboxFailure.PortFailure)]
    [InlineData((int)So6120MailboxFailure.Cancelled)]
    public void PendingCommandStopsOnReplacementPortErrorOrCancellation(int failure)
    {
        var expected = (So6120MailboxFailure)failure;
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        var fake = new Fake { LeaveCommandBusy = true };
        int waits = 0;
        var result = Core(fake, clock, afterWait: () =>
        {
            waits++;
            switch (expected)
            {
                case So6120MailboxFailure.CommandChanged: fake.Mailbox[0x93] = 0xD0; break;
                case So6120MailboxFailure.PortFailure: fake.FailAt = fake.Calls + 2; break;
                case So6120MailboxFailure.Cancelled: cancellation.Cancel(); break;
            }
        }).WriteAndConfirm(So6120MailboxRegister.StagedDuty2, 64, cancellation.Token);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(1, waits);
        Assert.Equal(1, result.CommandsPossiblySubmitted);
        Assert.Equal(0, result.CommandsCompleted);
        Assert.Equal(new byte[] { 0xA1 }, fake.Commands);
        Assert.Equal("poll", result.Operations[^1].Phase);
    }

    [Fact]
    public void PollLimitDoesNotClearCommandDespiteEarlySuccessStatus()
    {
        var fake = new Fake { LeaveCommandBusy = true };
        // Keep the clock still to exercise the hard count bound independently
        // of elapsed time and ensure no recovery command is published.
        var result = new So6120HostMailboxTransaction(fake, fake, new Clock(), maxPolls: 3,
            waitForNextPoll: (_, _) => { }).Read(So6120MailboxRegister.Selector);
        Assert.Equal(So6120MailboxFailure.PollLimit, result.Failure);
        Assert.Equal(0, result.CommandsCompleted);
        Assert.Equal(3, result.Operations.Count(item => item.Phase == "poll" && item.Operation == So6120PortOperation.ReadData));
        Assert.Single(fake.Commands);
        Assert.Equal((byte)0xA0, fake.Mailbox[0x93]);
    }

    [Fact]
    public void ReplacedCommandStopsWithoutFurtherPolling()
    {
        var fake = new Fake { ReplacementCommand = 0xD0 };
        var result = Core(fake).Read(So6120MailboxRegister.Selector);
        Assert.Equal(So6120MailboxFailure.CommandChanged, result.Failure);
        Assert.Single(fake.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeOrDuringPublicationPreservesSubmissionEvidence(bool during)
    {
        using var cancel = new CancellationTokenSource();
        var fake = new Fake();
        if (during) { fake.AfterPublish = cancel.Cancel; }
        else { cancel.Cancel(); }
        var result = Core(fake).WriteAndConfirm(So6120MailboxRegister.Manual, 1, cancel.Token);
        Assert.Equal(So6120MailboxFailure.Cancelled, result.Failure);
        Assert.Equal(during ? 1 : 0, result.CommandsPossiblySubmitted);
        Assert.Equal(during ? 1 : 0, fake.Commands.Count);
        if (!during) { Assert.Equal(0, fake.Calls); }
        else { Assert.Equal("publish", result.Operations[^1].Phase); }
    }

    [Fact]
    public void DeadlineAfterPublicationStopsWithoutReadbackOrCleanup()
    {
        var clock = new Clock();
        var fake = new Fake { AfterPublish = () => clock.Ticks = TimeSpan.TicksPerSecond };
        var result = Core(fake, clock).WriteAndConfirm(So6120MailboxRegister.Manual, 1);
        Assert.Equal(So6120MailboxFailure.Deadline, result.Failure);
        Assert.Equal(1, result.CommandsPossiblySubmitted);
        Assert.Single(fake.Commands);
        Assert.Equal("publish", result.Operations[^1].Phase);
    }

    [Fact]
    public void ConfirmationAdmissionFailureRetainsCompletedWriteWithoutClaimingConfirmation()
    {
        var fake = new Fake { RefuseConfirmation = true };
        var result = Core(fake).WriteAndConfirm(So6120MailboxRegister.Override, 2);
        Assert.Equal(So6120MailboxFailure.AdmissionRefused, result.Failure);
        Assert.True(result.WriteHandlerCompleted);
        Assert.False(result.WriteConfirmed);
        Assert.Equal(1, result.CommandsCompleted);
        Assert.Equal(1, result.CommandsPossiblySubmitted);
        Assert.Single(fake.Commands);
    }

    private static So6120HostMailboxTransaction Core(Fake fake, Clock? clock = null, TimeSpan? timeout = null,
        Action? afterWait = null)
    {
        clock ??= new Clock();
        return new(fake, fake, clock, timeout, waitForNextPoll: (milliseconds, _) =>
        {
            clock.Ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
            afterWait?.Invoke();
        });
    }

    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    private sealed class Fake : ISo6120MailboxBytePort, ISo6120MailboxPrerequisites
    {
        public byte[] Mailbox { get; } = new byte[256];
        public byte[] Registers { get; } = new byte[256];
        public List<(byte Index, byte Value)> Writes { get; } = [];
        public List<byte> Commands { get; } = [];
        public List<string> Events { get; } = [];
        public List<So6120MailboxRequest> Prepared { get; } = [];
        public int Calls { get; private set; }
        public int FailAt, ThrowAt;
        public uint ReadCount = 1;
        public So6120PortEffect FailureEffect = So6120PortEffect.MayHavePerformed;
        public bool RefusePreparation, RefusePublication, RefuseConfirmation;
        public bool SkipMappedWrite, LosePublishResult, LeaveCommandBusy;
        public byte? CorruptDescriptor, ReplacementCommand;
        public byte CompletionStatus = 0x80;
        public Action? AfterPublish;
        public Action? AfterAdmission;
        private byte index;

        public bool Prepare(So6120MailboxRequest request)
        {
            Prepared.Add(request);
            Events.Add("prepare");
            return !RefusePreparation && !(RefuseConfirmation && Prepared.Count == 2);
        }
        public bool AdmitPublication(So6120MailboxRequest request)
        {
            Assert.Same(Prepared[^1], request);
            Events.Add("admit");
            AfterAdmission?.Invoke();
            return !RefusePublication;
        }
        public So6120PortResult SelectIndex(byte value) => Operation(() => { index = value; return 0; }, false);
        public So6120PortResult ReadData() => Operation(() => Mailbox[index], true);
        public So6120PortResult WriteData(byte value) => Operation(() =>
        {
            Mailbox[index] = value;
            Writes.Add((index, value));
            if (index == 0x93)
            {
                Commands.Add(value);
                Events.Add("publish");
                Assert.Equal((byte)0, Mailbox[0x90]);
                Assert.Equal((byte)1, Mailbox[0x92]);
                Assert.Equal((byte)0, Mailbox[0x94]);
                if (value == 0xA1 && !SkipMappedWrite) { Registers[Mailbox[0x91]] = Mailbox[0xA0]; }
                if (value == 0xA0) { Mailbox[0xA0] = Registers[Mailbox[0x91]]; }
                Mailbox[0x94] = CompletionStatus;
                if (!LeaveCommandBusy) { Mailbox[0x93] = ReplacementCommand ?? 0; }
                if (CorruptDescriptor is byte field) { Mailbox[field]++; }
                AfterPublish?.Invoke();
            }
            return 0;
        }, false, index == 0x93);
        private So6120PortResult Operation(Func<byte> operation, bool read, bool publish = false)
        {
            Calls++;
            if (Calls == ThrowAt) { throw new IOException("Injected port failure"); }
            if (Calls == FailAt) { return new(false, 0, 0, 1234, FailureEffect); }
            byte value = operation();
            if (publish && LosePublishResult) { return new(false, 10, 0, 1234, So6120PortEffect.MayHavePerformed); }
            return new(true, read ? ReadCount : 10, value, 0, So6120PortEffect.Performed);
        }
    }
}
