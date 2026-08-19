namespace OpenBlade.Service;

public sealed class LowPowerGate
{
    private readonly object sync = new();
    private int active;
    private long epoch;

    public bool IsActive => Volatile.Read(ref active) != 0;

    public bool Enter()
    {
        lock (sync)
        {
            bool changed = active == 0;
            Volatile.Write(ref active, 1);
            if (changed)
            {
                epoch++;
            }

            return changed;
        }
    }

    public bool Exit()
    {
        lock (sync)
        {
            bool changed = active != 0;
            Volatile.Write(ref active, 0);
            if (changed)
            {
                epoch++;
            }

            return changed;
        }
    }

    internal bool TryAcquireOperation(out LowPowerOperationPermit permit)
    {
        lock (sync)
        {
            if (active != 0)
            {
                permit = default;
                return false;
            }

            permit = new LowPowerOperationPermit(this, epoch);
            return true;
        }
    }

    internal bool IsCurrent(LowPowerOperationPermit permit)
    {
        lock (sync)
        {
            return active == 0 && permit.Matches(this, epoch);
        }
    }
}

internal readonly struct LowPowerOperationPermit(
    LowPowerGate owner,
    long epoch)
{
    internal bool Matches(LowPowerGate expectedOwner, long expectedEpoch) =>
        ReferenceEquals(owner, expectedOwner) && epoch == expectedEpoch;
}
