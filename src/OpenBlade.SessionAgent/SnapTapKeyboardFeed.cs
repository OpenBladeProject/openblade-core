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

    /// <summary>Called on the observer's message thread; never blocks or throws.</summary>
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
    /// </summary>
    internal void Drain(SnapTapDeliveryTracker tracker)
    {
        while (packets.TryDequeue(out var packet))
        {
            Interlocked.Decrement(ref count);
            tracker.Observe(packet.MakeCode, packet.IsUp);
        }
        if (Interlocked.Exchange(ref overflowed, 0) != 0)
        {
            tracker.Clear();
        }
        tracker.Pumped();
    }
}
