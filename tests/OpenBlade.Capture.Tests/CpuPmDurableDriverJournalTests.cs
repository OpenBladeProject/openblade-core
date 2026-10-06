using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

// In-memory durable-write simulation only; never constructs the Windows journal.
public sealed class CpuPmDurableDriverJournalTests
{
    private static CpuPmDriverOwnerTag Owner() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), new('A', 64), new('b', 64));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    [Fact]
    public void FullLifecycleIsDurableAndBoundedBeforeEverySuccessfulReturn()
    {
        var storage = new Storage(); var owner = Owner(); var journal = new CpuPmDurableDriverJournal(owner, storage); int sequence = 0;
        foreach (var phase in Enum.GetValues<CpuPmDriverEvent>())
        {
            Assert.True(journal.AppendAndFlush(new(++sequence, phase, owner)));
            Assert.Equal(sequence, storage.Flushes); Assert.Equal(sequence, storage.Verifications);
        }
        Assert.Equal(1, storage.Creates); Assert.False(journal.IsRefused); Assert.True(storage.Bytes.Length <= CpuPmDurableDriverJournal.MaximumBytes);
        string[] lines = Encoding.UTF8.GetString(storage.Bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries); Assert.Equal(13, lines.Length);
        for (int index = 0; index < lines.Length; index++)
        {
            using var record = JsonDocument.Parse(lines[index]); Assert.Equal(6, record.RootElement.EnumerateObject().Count());
            Assert.Equal(index + 1, record.RootElement.GetProperty("sequence").GetInt32()); Assert.Equal(owner.Value, record.RootElement.GetProperty("owner").GetString());
            Assert.Equal(Enum.GetValues<CpuPmDriverEvent>()[index].ToString(), record.RootElement.GetProperty("phase").GetString());
        }
        Assert.False(journal.AppendAndFlush(new(14, CpuPmDriverEvent.CreateIntent, owner))); Assert.Equal(13, storage.Appends);
    }
    [Fact]
    public void ConclusiveNoChildPathAllowsCleanupWithoutInventingLaunchEvents()
    {
        var storage = new Storage(); var journal = new CpuPmDurableDriverJournal(Owner(), storage); int sequence = 0;
        foreach (var phase in Enum.GetValues<CpuPmDriverEvent>().Where(p => p is not (CpuPmDriverEvent.ChildLaunchIntent or CpuPmDriverEvent.ChildBound)))
        { Assert.True(journal.AppendAndFlush(new(++sequence, phase, Owner()))); }
        Assert.Equal(11, storage.Appends);
    }
    [Theory]
    [InlineData("run")]
    [InlineData("lifetime")]
    [InlineData("nonce")]
    [InlineData("payload")]
    [InlineData("sequence")]
    [InlineData("phase")]
    public void WrongOwnerSequenceOrPhaseRefusesBeforeCreatingFile(string fault)
    {
        var storage = new Storage(); var journal = new CpuPmDurableDriverJournal(Owner(), storage); var owner = Owner();
        owner = new(fault == "run" ? Guid.NewGuid() : owner.Run, fault == "lifetime" ? Guid.NewGuid() : owner.Lifetime,
            new(fault == "payload" ? 'C' : 'A', 64), new(fault == "nonce" ? 'c' : 'b', 64));
        Assert.False(journal.AppendAndFlush(new(fault == "sequence" ? 2 : 1, fault == "phase" ? CpuPmDriverEvent.Started : CpuPmDriverEvent.CreateIntent, owner)));
        Assert.Equal(0, storage.Creates); Assert.Equal(0, storage.Appends);
        Assert.False(journal.AppendAndFlush(new(1, CpuPmDriverEvent.CreateIntent, Owner()))); Assert.Equal(0, storage.Creates);
    }
    [Theory]
    [InlineData("create-collision")]
    [InlineData("partial-write")]
    [InlineData("flush")]
    [InlineData("readback")]
    [InlineData("pin-loss")]
    public void UncertainCreateWriteFlushOrReadbackPermanentlyRefusesWithoutRetry(string fault)
    {
        var storage = new Storage { Fault = fault }; var journal = new CpuPmDurableDriverJournal(Owner(), storage);
        Assert.False(journal.AppendAndFlush(new(1, CpuPmDriverEvent.CreateIntent, Owner()))); Assert.True(journal.IsRefused);
        int creates = storage.Creates, appends = storage.Appends;
        storage.Fault = null;
        Assert.False(journal.AppendAndFlush(new(1, CpuPmDriverEvent.CreateIntent, Owner())));
        Assert.False(journal.AppendAndFlush(new(2, CpuPmDriverEvent.Created, Owner())));
        Assert.Equal(creates, storage.Creates); Assert.Equal(appends, storage.Appends);
    }
    [Fact]
    public void ChangedPriorPrefixRefusesFollowingAppendAndRetainsPartialEvidence()
    {
        var storage = new Storage(); var journal = new CpuPmDurableDriverJournal(Owner(), storage);
        Assert.True(journal.AppendAndFlush(new(1, CpuPmDriverEvent.CreateIntent, Owner()))); storage.Bytes[0] ^= 1;
        Assert.False(journal.AppendAndFlush(new(2, CpuPmDriverEvent.Created, Owner()))); Assert.Equal(1, storage.Flushes);
        Assert.True(journal.IsRefused); Assert.NotEmpty(storage.Bytes);
    }
    [Fact]
    public void ReentrantAppendCannotPromoteTheOuterCompletedWrite()
    {
        var storage = new Storage(); var journal = new CpuPmDurableDriverJournal(Owner(), storage);
        storage.OnAppend = () => Assert.False(journal.AppendAndFlush(new(2, CpuPmDriverEvent.Created, Owner())));
        Assert.False(journal.AppendAndFlush(new(1, CpuPmDriverEvent.CreateIntent, Owner())));
        Assert.True(journal.IsRefused); Assert.Equal(1, storage.Appends); Assert.Equal(1, storage.Flushes);
        Assert.False(journal.AppendAndFlush(new(2, CpuPmDriverEvent.Created, Owner()))); Assert.Equal(1, storage.Appends);
    }
    [Fact]
    public void MutationIntentCannotSkipItsReturnedObservation()
    {
        var storage = new Storage(); var journal = new CpuPmDurableDriverJournal(Owner(), storage);
        Assert.True(journal.AppendAndFlush(new(1, CpuPmDriverEvent.CreateIntent, Owner())));
        Assert.False(journal.AppendAndFlush(new(2, CpuPmDriverEvent.StartIntent, Owner()))); Assert.Equal(1, storage.Appends);
        Assert.Equal(0x80200000u, WindowsCpuPmDriverJournalStorage.WriteThroughOpenReparse);
    }
    private sealed class Storage : ICpuPmDriverJournalStorage
    {
        internal byte[] Bytes = []; internal string? Fault; internal int Creates, Appends, Flushes, Verifications; internal Action? OnAppend;
        public void CreateFreshAndPin() { Creates++; if (Fault == "create-collision") { throw new IOException(); } }
        public void AppendFlushAndVerify(byte[] line, int previousLength, string previousSha256, string nextSha256)
        {
            Appends++; OnAppend?.Invoke();
            if (Fault == "pin-loss" || Bytes.Length != previousLength || Hash(Bytes) != previousSha256) { throw new IOException(); }
            Bytes = Bytes.Concat(Fault == "partial-write" ? line[..3] : line).ToArray();
            if (Fault == "partial-write" || Fault == "flush") { throw new IOException(); }
            Flushes++;
            if (Fault == "readback" || Hash(Bytes) != nextSha256) { throw new IOException(); }
            Verifications++;
        }
    }
}
