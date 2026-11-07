using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Capture;

internal interface ICpuPmDriverJournalStorage
{
    void CreateFreshAndPin();
    // Validate the old prefix, append once, write-through + Flush(true), and
    // validate the new length/hash using the SAME retained file handle.
    void AppendFlushAndVerify(byte[] line, int previousLength, string previousSha256, string nextSha256);
}

// A durable intent log, never a recovery/adoption API. No reader can reconstruct
// an owned SCM handle, successful Start or lifetime authority from these bytes.
internal sealed class CpuPmDurableDriverJournal : ICpuPmDriverJournal
{
    internal const int MaximumBytes = 16384, MaximumLineBytes = 1024, MaximumRecords = 13;
    private readonly CpuPmDriverOwnerTag owner;
    private readonly ICpuPmDriverJournalStorage storage;
    private byte[] committed = [];
    private CpuPmDriverEvent? last;
    private int sequence, busy, refused;
    private bool created;
    internal bool IsRefused => Volatile.Read(ref refused) != 0;
    internal CpuPmDurableDriverJournal(CpuPmDriverOwnerTag owner, ICpuPmDriverJournalStorage storage)
    { this.owner = owner ?? throw new ArgumentNullException(nameof(owner)); this.storage = storage ?? throw new ArgumentNullException(nameof(storage)); }
    public bool AppendAndFlush(CpuPmDriverLifecycleRecord record)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { Volatile.Write(ref refused, 1); return false; }
        try
        {
            if (IsRefused || record is null || record.Owner != owner || sequence >= MaximumRecords || record.Sequence != sequence + 1
                || !AllowedNext(last, record.Event)) { return Refuse(); }
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                sequence = record.Sequence,
                run = owner.Run.ToString("N"),
                lifetime = owner.Lifetime.ToString("N"),
                owner = owner.Value,
                phase = record.Event.ToString()
            });
            byte[] line = new byte[json.Length + 1]; json.CopyTo(line, 0); line[^1] = (byte)'\n';
            if (line.Length > MaximumLineBytes || line.Length > MaximumBytes - committed.Length) { return Refuse(); }
            byte[] next = new byte[committed.Length + line.Length]; committed.CopyTo(next, 0); line.CopyTo(next, committed.Length);
            if (!created) { created = true; storage.CreateFreshAndPin(); }
            if (IsRefused) { return false; }
            storage.AppendFlushAndVerify((byte[])line.Clone(), committed.Length, Hash(committed), Hash(next));
            if (IsRefused) { return false; }
            committed = next; sequence = record.Sequence; last = record.Event; return true;
        }
        catch (Exception) { return Refuse(); }
        finally { Volatile.Write(ref busy, 0); }
    }
    private bool Refuse() { Volatile.Write(ref refused, 1); return false; }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static bool AllowedNext(CpuPmDriverEvent? previous, CpuPmDriverEvent next) => previous switch
    {
        null => next == CpuPmDriverEvent.CreateIntent,
        CpuPmDriverEvent.CreateIntent => next == CpuPmDriverEvent.Created,
        CpuPmDriverEvent.Created => next == CpuPmDriverEvent.TagIntent,
        CpuPmDriverEvent.TagIntent => next == CpuPmDriverEvent.Tagged,
        CpuPmDriverEvent.Tagged => next == CpuPmDriverEvent.StartIntent,
        CpuPmDriverEvent.StartIntent => next == CpuPmDriverEvent.Started,
        CpuPmDriverEvent.Started => next is CpuPmDriverEvent.ChildLaunchIntent or CpuPmDriverEvent.StopIntent,
        CpuPmDriverEvent.ChildLaunchIntent => next == CpuPmDriverEvent.ChildBound,
        CpuPmDriverEvent.ChildBound => next == CpuPmDriverEvent.StopIntent,
        CpuPmDriverEvent.StopIntent => next == CpuPmDriverEvent.Stopped,
        CpuPmDriverEvent.Stopped => next == CpuPmDriverEvent.DeleteIntent,
        CpuPmDriverEvent.DeleteIntent => next == CpuPmDriverEvent.Deleted,
        CpuPmDriverEvent.Deleted => next == CpuPmDriverEvent.AbsenceVerified,
        _ => false,
    };
}
