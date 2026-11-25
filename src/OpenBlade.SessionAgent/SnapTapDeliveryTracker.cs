using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent;

/// <summary>Confirms that keys sent back through the driver reach Windows input.</summary>
/// <remarks>
/// The driver consumes hooked keys and the worker sends them back through the
/// driver's own output path. That path can accept a key without delivering it, so
/// each sent key must appear in the worker's Raw Input before a deadline. Only
/// keyboard events from a real device count; SendInput events carry no device.
/// A matching key from another keyboard can satisfy an expectation, which hides a
/// loss but never reports a false one.
/// </remarks>
internal sealed class SnapTapDeliveryTracker
{
    // Both bounds must pass before a key counts as lost: the deadline covers
    // driver and Raw Input latency, including a Windows input thread stalled by a
    // slow low-level hook, and the pump count keeps a starved worker that has not
    // yet drained its queue from blaming the driver. With a shared Fn observer a
    // pump counts only after that observer's thread has caught up with its own
    // Raw Input queue, so a lagging observer cannot make a key look lost. Checks
    // run only on worker passes, which input drives. A false loss turns the
    // feature off until the user retries, so the deadline is generous.
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    internal const int MinimumPumps = 5;
    internal const int Capacity = 256;

    private readonly List<Pending> pending = [];
    private long pumps;
    private int sent;
    private int delivered;

    internal int PendingCount => pending.Count;

    internal void Expect(SnapTapKeyStroke stroke, TimeSpan now)
    {
        sent++;
        pending.Add(new(stroke.ScanCode, stroke.IsDown, now, pumps));
    }

    /// <summary>Matches the oldest pending key with the same scan code and direction.</summary>
    internal void Observe(ushort makeCode, bool isUp)
    {
        int index = pending.FindIndex(entry => entry.ScanCode == makeCode && entry.IsDown != isUp);
        if (index >= 0)
        {
            pending.RemoveAt(index);
            delivered++;
        }
    }

    internal void Pumped() => pumps++;

    // A worker pass this far apart from the previous one means the process, or
    // the system, was frozen; keys sent before it get a fresh deadline.
    internal static readonly TimeSpan FreezeGap = TimeSpan.FromMilliseconds(500);

    /// <summary>Restarts the deadline and pump count of every pending key.</summary>
    internal void Rebase(TimeSpan now)
    {
        for (int index = 0; index < pending.Count; index++)
        {
            pending[index] = pending[index] with { SentAt = now, Pump = pumps };
        }
    }

    /// <summary>Drops pending keys whose delivery cannot be observed, such as input to an elevated window.</summary>
    internal void Clear() => pending.Clear();

    internal DiagnosticFailure? Check(TimeSpan now)
    {
        bool overflow = pending.Count > Capacity;
        if (!overflow && (pending.Count == 0 || now - pending[0].SentAt < Deadline
            || pumps - pending[0].Pump < MinimumPumps))
        {
            return null;
        }
        // The driver may still deliver these keys later, so the output is uncertain.
        // Counts only: diagnostics never record which keys were pressed.
        return SnapTapDiagnosticFailure.Create(
            SnapTapDiagnosticFailure.Worker, "Run", "InputLoop", "OutputNotDelivered", true) with
        {
            Context = $"Sent={sent}; Delivered={delivered}; Pending={pending.Count}; "
                + $"OldestAgeMs={(long)(now - pending[0].SentAt).TotalMilliseconds}; "
                + $"Pumps={pumps - pending[0].Pump}; Overflow={overflow}",
        };
    }

    private readonly record struct Pending(ushort ScanCode, bool IsDown, TimeSpan SentAt, long Pump);
}
