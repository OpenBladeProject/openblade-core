using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Capture;
using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class So6120DirectDutyJournalTests
{
    [Fact]
    public void ConfirmedRoundTripRetainsBindingAndNeverGrantsRestart()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Create())
        {
            fixture.Initialize(journal);
            fixture.Change(journal, So6120MailboxRegister.StagedDuty1, 30, So6120DutyOwnedFields.Staged1);
            fixture.Change(journal, So6120MailboxRegister.StagedDuty2, 40, So6120DutyOwnedFields.Staged2);
            fixture.Change(journal, So6120MailboxRegister.Override, 0x51, So6120DutyOwnedFields.Override);
            fixture.Change(journal, So6120MailboxRegister.Selector, 0xFE, So6120DutyOwnedFields.Selector);
            journal.ClaimExit(fixture.Checkpoint!);
            fixture.Change(journal, So6120MailboxRegister.Selector, 5, So6120DutyOwnedFields.Selector, true);
            fixture.Change(journal, So6120MailboxRegister.Override, 0x11, So6120DutyOwnedFields.Override, true);
            fixture.Change(journal, So6120MailboxRegister.StagedDuty1, 10, So6120DutyOwnedFields.Staged1, true);
            fixture.Change(journal, So6120MailboxRegister.StagedDuty2, 20, So6120DutyOwnedFields.Staged2, true);
            fixture.Observe(journal);
            journal.Complete(fixture.Identity.Owner, fixture.Checkpoint!.Generation, fixture.Latest!.Sequence);
        }
        var recovered = So6120DirectDutyJournal.Inspect(fixture.Root);
        Assert.True(recovered.Complete);
        Assert.False(recovered.Corrupt);
        Assert.False(recovered.UnresolvedWork);
        Assert.False(recovered.CanResume);
        Assert.True(recovered.PermissionPublicationPossible);
        Assert.True(recovered.PermissionRestoredConfirmed);
        Assert.Equal(fixture.Identity, recovered.Identity);
        Assert.Equal(fixture.Baseline, recovered.LastConfirmedControls);
        Assert.Equal(9, recovered.CheckpointGeneration);
        Assert.Throws<IOException>(() => fixture.Create());
    }

    [Fact]
    public void ExclusiveOwnershipBlocksOtherWriterAndInspectionUntilDisposal()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Create())
        {
            Assert.Throws<IOException>(() => fixture.Create());
            Assert.Throws<IOException>(() => So6120DirectDutyJournal.Inspect(fixture.Root));
        }
        var recovered = So6120DirectDutyJournal.Inspect(fixture.Root);
        Assert.True(recovered.UnresolvedWork);
        Assert.False(recovered.Complete);
        Assert.False(recovered.CanResume);
        Assert.False(recovered.Corrupt);
        Assert.Throws<IOException>(() => fixture.Create());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RejectsUnboundOrInvalidIdentityBeforeCreatingJournal(int field)
    {
        using var fixture = new Fixture();
        var identity = field switch
        {
            0 => fixture.Identity with { DeviceEvidenceSha256 = "invalid" },
            1 => fixture.Identity with { ConsentId = Guid.Empty },
            2 => fixture.Identity with { CapturedProfile = PerformancePreset.Custom },
            _ => fixture.Identity with { BootIdentity = new string('x', 257) },
        };
        Assert.Throws<InvalidDataException>(() => So6120DirectDutyJournal.Create(fixture.Root, identity));
        Assert.False(File.Exists(fixture.Path));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("C:\\")]
    [InlineData("C:\\test\\..\\other")]
    [InlineData("C:\\test:stream")]
    [InlineData("\\\\server\\share\\run")]
    [InlineData("C:\\trailing.\\run")]
    public void RejectsUnboundedOrNonCanonicalPaths(string path)
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => So6120DirectDutyJournal.Create(path, fixture.Identity));
    }

    [Fact]
    public void OwnerMismatchPoisonsThisInstanceWithoutAppendingAuthority()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Create())
        {
            Assert.Throws<InvalidDataException>(() => journal.ClaimEntry(
                fixture.Identity.Owner with { OperationId = Guid.NewGuid() }, PerformancePreset.Silent));
            Assert.True(journal.Faulted);
            Assert.Throws<InvalidOperationException>(() => journal.RecordFence(fixture.Identity.Owner, So6120DutyFailure.JournalUnresolved));
        }
        Assert.Equal(1, So6120DirectDutyJournal.Inspect(fixture.Root).Records);
    }

    [Fact]
    public void IntentMustMatchBoundDutyAndNextField()
    {
        using var fixture = new Fixture();
        using var journal = fixture.Create();
        fixture.Initialize(journal);
        Assert.Throws<InvalidDataException>(() => journal.RecordIntent(fixture.Identity.Owner,
            new(So6120MailboxRegister.Override, 0x51, fixture.Controls), fixture.Latest!));
        Assert.True(journal.Faulted);
    }

    [Fact]
    public void ObservationReplayAndCheckpointJumpAreRejected()
    {
        using var fixture = new Fixture();
        using var journal = fixture.Create();
        fixture.Initialize(journal);
        Assert.Throws<InvalidDataException>(() => journal.RecordObservation(fixture.Latest!));
        using var second = new Fixture();
        using var otherJournal = second.Create();
        second.Initialize(otherJournal);
        Assert.Throws<InvalidDataException>(() => otherJournal.RecordCheckpoint(second.Checkpoint! with { Generation = 3 }));
    }

    [Fact]
    public void DuplicatePublicationCannotBeUsedAsAnotherCommand()
    {
        using var fixture = new Fixture();
        using var journal = fixture.Create();
        fixture.Initialize(journal);
        var request = fixture.BeginRead(journal);
        journal.RecordPublication(request.Id);
        Assert.Throws<InvalidDataException>(() => journal.RecordPublication(request.Id));
    }

    [Fact]
    public void ReadPublicationAndRawNativeOutcomeAreStoredWithoutNormalizingWriteCount()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Create())
        {
            fixture.Initialize(journal);
            var request = fixture.BeginRead(journal);
            journal.RecordPublication(request.Id);
            journal.RecordTransferResult(Success(17, request));
        }
        Assert.False(So6120DirectDutyJournal.Inspect(fixture.Root).Corrupt);
        // The synthetic boundary retains driver Information=10 for writes.
        Assert.Contains("\"BytesReturned\":10", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(fixture.Path)));
    }

    [Fact]
    public void MalformedReadResultCannotBecomeConfirmedTransfer()
    {
        using var fixture = new Fixture();
        using var journal = fixture.Create();
        fixture.Initialize(journal);
        var request = fixture.BeginRead(journal);
        journal.RecordPublication(request.Id);
        var result = Success(17, request);
        result = result with
        {
            Operations = [.. result.Operations, new(request.Id, "completion", So6120PortOperation.ReadData, null,
                new(true, 2, 17, 0, So6120PortEffect.Performed), null)],
        };
        Assert.Throws<InvalidDataException>(() => journal.RecordTransferResult(result));
    }

    [Fact]
    public void PendingPermissionPublicationRemainsUnresolvedAfterRelease()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Create())
        {
            fixture.Initialize(journal);
            fixture.Change(journal, So6120MailboxRegister.StagedDuty1, 30, So6120DutyOwnedFields.Staged1);
            fixture.Change(journal, So6120MailboxRegister.StagedDuty2, 40, So6120DutyOwnedFields.Staged2);
            var request = fixture.BeginWrite(journal, So6120MailboxRegister.Override, 0x51);
            journal.RecordPublication(request.Id);
        }
        var recovered = So6120DirectDutyJournal.Inspect(fixture.Root);
        Assert.True(recovered.PermissionPublicationPossible);
        Assert.False(recovered.PermissionRestoredConfirmed);
        Assert.True(recovered.UnresolvedWork);
        Assert.False(recovered.CanResume);
    }

    [Fact]
    public void AmbiguousTransferDurablyFencesEvenFurtherReads()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Create())
        {
            fixture.Initialize(journal);
            var request = fixture.BeginRead(journal);
            journal.RecordPublication(request.Id);
            journal.RecordTransferResult(Unsubmitted(request, So6120PortEffect.MayHavePerformed) with { CommandsPossiblySubmitted = 1 });
            journal.RecordFence(fixture.Identity.Owner, So6120DutyFailure.CommandUnresolved);
            Assert.Throws<InvalidDataException>(() => fixture.BeginRead(journal));
        }
        var recovered = So6120DirectDutyJournal.Inspect(fixture.Root);
        Assert.True(recovered.Fenced);
        Assert.True(recovered.UnresolvedWork);
    }

    [Fact]
    public void KnownUnsubmittedEntryAllowsCapturedPartialExitButNoRetry()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Create())
        {
            fixture.Initialize(journal);
            fixture.Change(journal, So6120MailboxRegister.StagedDuty1, 30, So6120DutyOwnedFields.Staged1);
            var request = fixture.BeginWrite(journal, So6120MailboxRegister.StagedDuty2, 40);
            journal.RecordPublication(request.Id);
            journal.RecordTransferNotSubmitted(Unsubmitted(request, So6120PortEffect.NotPerformed));
            journal.RecordNotSubmitted(fixture.Identity.Owner, new(request.Register, 40, fixture.Controls), So6120DutyFailure.NotSubmitted);
            journal.ClaimExit(fixture.Checkpoint!);
            fixture.Change(journal, So6120MailboxRegister.StagedDuty1, 10, So6120DutyOwnedFields.Staged1, true);
            journal.Complete(fixture.Identity.Owner, fixture.Checkpoint!.Generation, fixture.Latest!.Sequence);
        }
        Assert.True(So6120DirectDutyJournal.Inspect(fixture.Root).Complete);
    }

    [Fact]
    public void PossibleSubmissionCannotUseKnownUnsubmittedResolution()
    {
        using var fixture = new Fixture();
        using var journal = fixture.Create();
        fixture.Initialize(journal);
        var request = fixture.BeginRead(journal);
        journal.RecordPublication(request.Id);
        Assert.Throws<InvalidDataException>(() => journal.RecordTransferNotSubmitted(Unsubmitted(request, So6120PortEffect.MayHavePerformed)));
    }

    [Fact]
    public void InvalidResultBoundsAlsoPoisonTheInstance()
    {
        using var fixture = new Fixture();
        using var journal = fixture.Create();
        fixture.Initialize(journal);
        var request = fixture.BeginRead(journal);
        Assert.Throws<InvalidDataException>(() => journal.RecordTransferResult(Success(17, request) with { CommandsPossiblySubmitted = 3 }));
        Assert.True(journal.Faulted);
        Assert.Throws<InvalidOperationException>(() => journal.RecordPublication(request.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void StorageFailureNeverBecomesSuccessfulFenceOrReusableInstance(int stageValue)
    {
        var stage = (So6120JournalWriteStage)stageValue;
        using var fixture = new Fixture();
        bool armed = false;
        using (var journal = So6120DirectDutyJournal.Create(fixture.Root, fixture.Identity,
            current => { if (armed && current == stage) { throw new IOException("synthetic storage fault"); } }))
        {
            armed = true;
            Assert.Throws<IOException>(() => journal.ClaimEntry(fixture.Identity.Owner, PerformancePreset.Silent));
            Assert.True(journal.Faulted);
            Assert.Throws<InvalidOperationException>(() => journal.RecordFence(fixture.Identity.Owner, So6120DutyFailure.JournalUnresolved));
        }
        Assert.True(File.Exists(fixture.Path));
        var recovered = So6120DirectDutyJournal.Inspect(fixture.Root);
        Assert.False(recovered.CanResume);
        Assert.True(recovered.UnresolvedWork);
        Assert.False(recovered.Complete);
    }

    [Fact]
    public void CreationFailureConsumesPathAndRetainsPartialEvidence()
    {
        using var fixture = new Fixture();
        Assert.Throws<IOException>(() => So6120DirectDutyJournal.Create(fixture.Root, fixture.Identity,
            _ => throw new IOException("synthetic storage fault")));
        Assert.True(File.Exists(fixture.Path));
        Assert.Throws<IOException>(() => fixture.Create());
        Assert.True(So6120DirectDutyJournal.Inspect(fixture.Root).UnresolvedWork);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(-1)]
    public void TruncatedTailNeverRestoresAuthority(int length)
    {
        using var fixture = new Fixture();
        fixture.Create().Dispose();
        using (var stream = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write))
        { stream.SetLength(length < 0 ? stream.Length - 1 : length); }
        var recovered = So6120DirectDutyJournal.Inspect(fixture.Root);
        Assert.True(recovered.Corrupt);
        Assert.True(recovered.UnresolvedWork);
        Assert.False(recovered.CanResume);
        Assert.True(recovered.PermissionPublicationPossible);
    }

    [Fact]
    public void CorruptHashAndOversizedFramesAreRejectedWithBoundedInspection()
    {
        using var fixture = new Fixture();
        fixture.Create().Dispose();
        byte[] bytes = File.ReadAllBytes(fixture.Path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(fixture.Path, bytes);
        Assert.True(So6120DirectDutyJournal.Inspect(fixture.Root).Corrupt);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(11, 4), So6120DirectDutyJournal.MaximumRecordBytes + 1);
        File.WriteAllBytes(fixture.Path, bytes);
        Assert.True(So6120DirectDutyJournal.Inspect(fixture.Root).Corrupt);
        using (var stream = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write))
        { stream.SetLength(So6120DirectDutyJournal.MaximumFileBytes + 1L); }
        Assert.True(So6120DirectDutyJournal.Inspect(fixture.Root).Corrupt);
    }

    private static So6120MailboxResult Success(byte value, params So6120MailboxRequest[] requests) => new(
        So6120MailboxFailure.None, value, requests.Length, requests.Length, requests.Length == 2,
        [.. requests.Select(x => new So6120MailboxPortObservation(x.Id, "publish", So6120PortOperation.WriteData, x.Command,
            new(true, 10, 0, 0, So6120PortEffect.Performed), null)),
            new(requests[^1].Id, "completion", So6120PortOperation.SelectIndex, 0xA0, new(true, 10, 0, 0, So6120PortEffect.Performed), null),
            new(requests[^1].Id, "completion", So6120PortOperation.ReadData, null, new(true, 1, value, 0, So6120PortEffect.Performed), null)]);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HashValidButIncompleteOrDuplicateJsonIsRejected(int damage)
    {
        using var fixture = new Fixture();
        fixture.Create().Dispose();
        byte[] original = File.ReadAllBytes(fixture.Path);
        int count = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(11, 4));
        string json = Encoding.UTF8.GetString(original, 15, count);
        if (damage == 0) { json = json[..^1] + ",\"Sequence\":1}"; }
        else
        {
            var node = JsonNode.Parse(json)!;
            if (damage == 1) { node.AsObject().Remove("Kind"); }
            else { node["Data"]!.AsObject().Remove("RequestedDuty1"); }
            json = node.ToJsonString();
        }
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] hash = SHA256.HashData([.. new byte[32], .. body]);
        using (var file = new FileStream(fixture.Path, FileMode.Create, FileAccess.Write))
        {
            file.Write(original.AsSpan(0, 11));
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, body.Length);
            file.Write(size);
            file.Write(body);
            file.Write(hash);
        }
        var recovered = So6120DirectDutyJournal.Inspect(fixture.Root);
        Assert.True(recovered.Corrupt);
        Assert.False(recovered.CanResume);
        Assert.Null(recovered.Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClaimedSuccessRequiresFinalPayloadReadMatchingValue(bool mismatched)
    {
        using var fixture = new Fixture();
        using var journal = fixture.Create();
        fixture.Initialize(journal);
        var request = fixture.BeginRead(journal);
        journal.RecordPublication(request.Id);
        var result = Success(17, request);
        result = mismatched
            ? result with { Value = 18 }
            : result with { Operations = [result.Operations[0]] };
        Assert.Throws<InvalidDataException>(() => journal.RecordTransferResult(result));
        Assert.True(journal.Faulted);
    }

    [Fact]
    public void ActualByteCoreCanPublishAndJournalAWriteWithIndependentConfirmationUsingFakePorts()
    {
        using var fixture = new Fixture();
        using (var journal = fixture.Create())
        {
            fixture.Initialize(journal);
            var mutation = new So6120DutyMutation(So6120MailboxRegister.StagedDuty1, 30, fixture.Controls);
            journal.RecordIntent(fixture.Identity.Owner, mutation, fixture.Latest!);
            journal.BeginTransfer(new(Guid.NewGuid(), mutation.Register, mutation.Value));
            var core = new So6120HostMailboxTransaction(new MemoryPort(), new JournalPrerequisites(journal), timeout: TimeSpan.FromSeconds(5));
            var result = core.WriteAndConfirm(mutation.Register, mutation.Value);
            Assert.True(result.WriteConfirmed);
            Assert.Equal(2, result.CommandsCompleted);
            journal.RecordTransferResult(result);
        }
        var recovered = So6120DirectDutyJournal.Inspect(fixture.Root);
        Assert.False(recovered.Corrupt);
        Assert.True(recovered.UnresolvedWork); // Byte match is not the full-state checkpoint.
    }

    private sealed class JournalPrerequisites(So6120DirectDutyJournal journal) : ISo6120MailboxPrerequisites
    {
        public bool Prepare(So6120MailboxRequest request) { journal.PrepareRequest(request); return true; }
        public bool AdmitPublication(So6120MailboxRequest request) { journal.RecordPublication(request.Id); return true; }
    }

    private sealed class MemoryPort : ISo6120MailboxBytePort
    {
        private readonly byte[] mailbox = new byte[256];
        private readonly byte[] mapped = new byte[256];
        private byte selected;
        public So6120PortResult SelectIndex(byte index) { selected = index; return new(true, 10, 0, 0, So6120PortEffect.Performed); }
        public So6120PortResult ReadData() => new(true, 1, mailbox[selected], 0, So6120PortEffect.Performed);
        public So6120PortResult WriteData(byte value)
        {
            mailbox[selected] = value;
            if (selected == 0x93)
            {
                if (value == 0xA1) { mapped[mailbox[0x91]] = mailbox[0xA0]; }
                else { mailbox[0xA0] = mapped[mailbox[0x91]]; }
                mailbox[0x94] = 0x80;
                mailbox[0x93] = 0;
            }
            return new(true, 10, 0, 0, So6120PortEffect.Performed);
        }
    }

    private static So6120MailboxResult Unsubmitted(So6120MailboxRequest request, So6120PortEffect effect) => new(
        So6120MailboxFailure.PortFailure, null, 0, 0, false,
        [new(request.Id, "publish", So6120PortOperation.WriteData, request.Command, new(false, 0, 0, 5, effect), null)]);

    // All device observations below are synthetic caller evidence; no port/native boundary is constructed.
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ob-journal-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, So6120DirectDutyJournal.FileName);
        public So6120JournalIdentity Identity { get; } = new(
            new(Guid.NewGuid(), "SO6120 synthetic device", "EC1.04 synthetic evidence", Guid.NewGuid()),
            PerformancePreset.Silent, 30, 40, new('a', 64), new('b', 64), Guid.NewGuid(), new('c', 64), "synthetic-boot");
        public So6120DutyControls Baseline { get; } = new(5, 0, 0x11, 10, 20, 183, 0);
        public So6120DutyControls Controls { get; private set; } = new(5, 0, 0x11, 10, 20, 183, 0);
        public So6120DutyObservation? Latest { get; private set; }
        public So6120DutyCheckpoint? Checkpoint { get; private set; }
        public So6120DirectDutyJournal Create() => So6120DirectDutyJournal.Create(Root, Identity);
        public void Initialize(So6120DirectDutyJournal journal)
        {
            journal.ClaimEntry(Identity.Owner, Identity.CapturedProfile);
            Observe(journal);
            Checkpoint = new(Identity.Owner, 1, Identity.CapturedProfile, Baseline, Controls, So6120DutyOwnedFields.None, Latest!);
            journal.RecordCheckpoint(Checkpoint);
        }
        public void Observe(So6120DirectDutyJournal journal)
        {
            long sequence = (Latest?.Sequence ?? 0) + 1;
            Latest = new(Identity.Owner, sequence, sequence * 10, sequence * 10 + 1, new(Controls, 50, 60, 20, 20, 0, 1));
            journal.RecordObservation(Latest);
        }
        public So6120MailboxRequest BeginRead(So6120DirectDutyJournal journal)
        {
            journal.BeginTransfer(new(Guid.NewGuid(), So6120MailboxRegister.ActualDuty1, null));
            var request = new So6120MailboxRequest(Guid.NewGuid(), So6120MailboxRegister.ActualDuty1, null);
            journal.PrepareRequest(request);
            return request;
        }
        public So6120MailboxRequest BeginWrite(So6120DirectDutyJournal journal, So6120MailboxRegister register, byte value)
        {
            Observe(journal);
            journal.RecordIntent(Identity.Owner, new(register, value, Controls), Latest!);
            journal.BeginTransfer(new(Guid.NewGuid(), register, value));
            var request = new So6120MailboxRequest(Guid.NewGuid(), register, value);
            journal.PrepareRequest(request);
            return request;
        }
        public void Change(So6120DirectDutyJournal journal, So6120MailboxRegister register, byte value,
            So6120DutyOwnedFields field, bool restoring = false)
        {
            var request = BeginWrite(journal, register, value);
            journal.RecordPublication(request.Id);
            var confirmation = new So6120MailboxRequest(Guid.NewGuid(), register, null);
            journal.PrepareRequest(confirmation);
            journal.RecordPublication(confirmation.Id);
            journal.RecordTransferResult(Success(value, request, confirmation));
            Controls = register switch
            {
                So6120MailboxRegister.StagedDuty1 => Controls with { Staged1 = value },
                So6120MailboxRegister.StagedDuty2 => Controls with { Staged2 = value },
                So6120MailboxRegister.Override => Controls with { Override = value },
                So6120MailboxRegister.Selector => Controls with { Selector = value },
                _ => throw new InvalidOperationException(),
            };
            Observe(journal);
            Checkpoint = Checkpoint! with
            {
                Generation = Checkpoint.Generation + 1,
                Expected = Controls,
                Owned = restoring ? Checkpoint.Owned & ~field : Checkpoint.Owned | field,
                Observation = Latest!,
            };
            journal.RecordCheckpoint(Checkpoint);
        }
        public void Dispose()
        {
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }
}
