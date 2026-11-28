using System.Globalization;
using System.IO.MemoryMappedFiles;

namespace OpenBlade.SessionAgent;

/// <summary>Diagnostics only: names the step a stalled keyboard worker was in.</summary>
/// <remarks>
/// The worker must not log from its input thread, and a killed worker cannot log at
/// all. It records its current step in shared memory with no I/O; the supervisor
/// reads that record when a heartbeat times out. Fields may tear across a concurrent
/// read, so nothing may gate on them.
/// </remarks>
internal sealed class SnapTapWorkerProgress : IDisposable
{
    internal static readonly string[] Stages =
    [
        "None", "WaitingForRelease", "BaselineRead", "BaselineValidation", "HookMutation",
        "HookReadback", "OwnershipRecord", "Subscription", "SubscriptionReadback", "InputLoop", "Cleanup",
    ];

    private const int StageOffset = 0;
    private const int HooksOffset = 4;
    private const int StartedOffset = 8;
    private const int AdmissionOffset = 16;
    private const int SlowestStageOffset = 24;
    private const int SlowestOffset = 32;
    private const int Size = 40;

    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly Func<long> clock;

    private SnapTapWorkerProgress(MemoryMappedFile map, Func<long>? clock)
    {
        this.map = map;
        this.clock = clock ?? (() => Environment.TickCount64);
        try
        {
            view = map.CreateViewAccessor(0, Size);
        }
        catch
        {
            map.Dispose();
            throw;
        }
    }

    internal static string Name(int parentId) =>
        string.Create(CultureInfo.InvariantCulture, $"Local\\OpenBlade.SnapTap.Progress.{parentId}");

    internal static SnapTapWorkerProgress Create(int parentId, Func<long>? clock = null) =>
        new(MemoryMappedFile.CreateOrOpen(Name(parentId), Size), clock);

    // A missing or inaccessible record only loses diagnostics.
    internal static SnapTapWorkerProgress? TryOpen(int parentId, Func<long>? clock = null)
    {
        try
        {
            return new(MemoryMappedFile.OpenExisting(Name(parentId), MemoryMappedFileRights.ReadWrite), clock);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void Reset()
    {
        for (int offset = 0; offset < Size; offset += sizeof(long))
        {
            view.Write(offset, 0L);
        }
    }

    // The writers never throw into the worker: a disposed record only loses diagnostics.
    internal void Enter(string stage)
    {
        int index = Array.IndexOf(Stages, stage);
        if (index <= 0) { return; }
        try
        {
            EnterCore(index);
        }
        catch (ObjectDisposedException) { }
    }

    private void EnterCore(int index)
    {
        long now = clock();
        int previous = view.ReadInt32(StageOffset);
        if (previous > 0)
        {
            long elapsed = now - view.ReadInt64(StartedOffset);
            if (elapsed > view.ReadInt64(SlowestOffset))
            {
                view.Write(SlowestOffset, elapsed);
                view.Write(SlowestStageOffset, previous);
            }
        }
        view.Write(StartedOffset, now);
        view.Write(StageOffset, index);
    }

    internal void HooksSet(int count) => Write(() => view.Write(HooksOffset, count));

    internal void Admission(long elapsedMilliseconds) => Write(() => view.Write(AdmissionOffset, elapsedMilliseconds));

    private static void Write(Action write)
    {
        try
        {
            write();
        }
        catch (ObjectDisposedException) { }
    }

    internal Snapshot Read()
    {
        int stage = view.ReadInt32(StageOffset);
        long started = view.ReadInt64(StartedOffset);
        int slowest = view.ReadInt32(SlowestStageOffset);
        return new(StageName(stage), stage > 0 ? clock() - started : 0, view.ReadInt32(HooksOffset),
            view.ReadInt64(AdmissionOffset), StageName(slowest), view.ReadInt64(SlowestOffset));
    }

    private static string StageName(int index) => index > 0 && index < Stages.Length ? Stages[index] : "None";

    public void Dispose()
    {
        view.Dispose();
        map.Dispose();
    }

    internal readonly record struct Snapshot(string Stage, long StageElapsedMilliseconds, int HooksSet,
        long LastAdmissionMilliseconds, string SlowestStage, long SlowestMilliseconds)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"WorkerStage={Stage}; StageElapsedMs={StageElapsedMilliseconds}; HooksSet={HooksSet}; "
            + $"LastAdmissionMs={LastAdmissionMilliseconds}; SlowestStage={SlowestStage}; SlowestMs={SlowestMilliseconds}");
    }
}
