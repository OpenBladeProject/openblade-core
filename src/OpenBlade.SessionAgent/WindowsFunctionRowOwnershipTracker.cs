namespace OpenBlade.SessionAgent;

/// <summary>Retains uncertain native ownership even when source construction throws.</summary>
internal sealed class WindowsFunctionRowOwnershipTracker
{
    private int outstandingSources;

    internal bool HasNoOutstandingSources => Volatile.Read(ref outstandingSources) == 0;

    internal Registration Begin()
    {
        Interlocked.Increment(ref outstandingSources);
        return new Registration(this);
    }

    internal sealed class Registration(WindowsFunctionRowOwnershipTracker owner)
    {
        private int state; // 0: starting, 1: active, 2: confirmed cleanup, 3: uncertain cleanup

        internal bool IsActive => Volatile.Read(ref state) == 1;
        internal bool IsCleanupConfirmed => Volatile.Read(ref state) == 2;

        internal bool CanTransferOwnership(bool sourceIsActiveAndIdle) => Volatile.Read(ref state) switch
        {
            1 => sourceIsActiveAndIdle && Volatile.Read(ref owner.outstandingSources) == 1,
            2 => owner.HasNoOutstandingSources,
            _ => false,
        };

        internal void ConfirmActive() => Interlocked.CompareExchange(ref state, 1, 0);

        internal void Complete(bool hookCleanupConfirmed, bool hasActiveKeys)
        {
            int next = hookCleanupConfirmed && !hasActiveKeys ? 2 : 3;
            int previous;
            do
            {
                previous = Volatile.Read(ref state);
                if (previous is 2 or 3)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref state, next, previous) != previous);

            if (next == 2)
            {
                Interlocked.Decrement(ref owner.outstandingSources);
            }
        }
    }
}
