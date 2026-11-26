using System.Collections.Concurrent;

namespace OpenBlade.SessionAgent;

/// <summary>Bounded hand-off of real keyboard packets from another thread's Raw Input sink.</summary>
internal sealed class SnapTapKeyboardFeed
{
    internal const int Capacity = 4096;
    private const ushort KeyBreak = 0x0001;

    private readonly ConcurrentQueue<(ushort MakeCode, bool IsUp)> packets = new();
    private int count;
    private int overflowed;
    private int caughtUp;
    private int lastCaughtUp;

    /// <summary>
    /// Called on the observer's thread once no Raw Input remains queued for it, so
    /// every packet Windows delivered before this point has been posted.
    /// </summary>
    internal void MarkCaughtUp() => Interlocked.Increment(ref caughtUp);

    /// <summary>Called on the observer's message thread; does not block.</summary>
    internal void Post(ushort makeCode, ushort flags)
    {
        if (Interlocked.Increment(ref count) > Capacity)
        {
            Interlocked.Decrement(ref count);
            Volatile.Write(ref overflowed, 1);
            return;
        }
        packets.Enqueue((makeCode, (flags & KeyBreak) != 0));
    }

    /// <summary>
    /// Moves queued packets into <paramref name="tracker"/>. A dropped packet could
    /// look like a lost key, so after an overflow the pending keys are not judged.
    /// A drain counts as a pump only when the observer has caught up since the
    /// previous one and this drain emptied the queue, so an observer thread that
    /// falls behind, for example after the process was briefly frozen, cannot make
    /// a delivered key look lost.
    /// </summary>
    internal void Drain(SnapTapDeliveryTracker tracker)
    {
        // Read before dequeuing: packets posted before this mark are already queued.
        int observed = Volatile.Read(ref caughtUp);
        bool emptied = false;
        for (int drained = 0; drained < Capacity; drained++)
        {
            if (!packets.TryDequeue(out var packet))
            {
                emptied = true;
                break;
            }
            Interlocked.Decrement(ref count);
            tracker.Observe(packet.MakeCode, packet.IsUp);
        }
        if (Interlocked.Exchange(ref overflowed, 0) != 0)
        {
            tracker.Clear();
        }
        if (emptied && observed != lastCaughtUp)
        {
            lastCaughtUp = observed;
            tracker.Pumped();
        }
    }
}
