namespace OpenBlade.Service;

public sealed class LowPowerGate
{
    private readonly object sync = new();
    private readonly TimeProvider timeProvider;
    private long latestTimestamp;
    private long boundaryTimestamp;
    // The low bit is the latch; the remaining bits distinguish a later suspend
    // after a resume, even if a preparation was waiting on hardware throughout.
    private long state;

    public LowPowerGate() : this(TimeProvider.System)
    {
    }

    internal LowPowerGate(TimeProvider timeProvider) =>
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal TimeProvider TimeProvider => timeProvider;

    internal event Action? TransitionChanged;

    internal long GetTimestamp() => timeProvider.GetTimestamp();

    internal LowPowerTransition CurrentTransition
    {
        get
        {
            lock (sync)
            {
                return new(state, boundaryTimestamp);
            }
        }
    }

    public bool IsActive => (Volatile.Read(ref state) & 1) != 0;

    public long Generation => Volatile.Read(ref state);

    public bool IsActiveForGeneration(long generation) =>
        (generation & 1) != 0 && Volatile.Read(ref state) == generation;

    public bool Enter() => Change(active: true);

    public bool Exit() => Change(active: false);

    private bool Change(bool active)
    {
        return Apply(active, GetTimestamp(), out _, out bool changed, newResume: false) && changed;
    }

    internal bool TryApply(bool active, long timestamp, out LowPowerTransition transition,
        bool newResume = false) => Apply(active, timestamp, out transition, out _, newResume);

    private bool Apply(bool active, long timestamp, out LowPowerTransition transition,
        out bool changed, bool newResume)
    {
        Action? changedCallback;
        lock (sync)
        {
            changed = false;
            // Both native callbacks and the authenticated relay use the same
            // machine QPC clock. A delayed suspend cannot undo a newer resume
            // or DisplayOn, even when the gate was already clear at that time.
            if (timestamp < latestTimestamp
                || (timestamp == latestTimestamp && active && (state & 1) == 0))
            {
                transition = new(state, boundaryTimestamp);
                return false;
            }

            long previousState = state;
            latestTimestamp = timestamp;
            if (((state & 1) != 0) != active)
            {
                boundaryTimestamp = timestamp;
                Volatile.Write(ref state, unchecked(state + 1));
            }
            else if (newResume && !active && boundaryTimestamp > 0
                && timeProvider.GetElapsedTime(boundaryTimestamp, timestamp) > TimeSpan.FromSeconds(2))
            {
                // A later resume may arrive without a corresponding suspend
                // in this process. Group only the adjacent duplicate notices.
                boundaryTimestamp = timestamp;
                Volatile.Write(ref state, unchecked(state + 2));
            }
            else if (boundaryTimestamp == 0)
            {
                boundaryTimestamp = timestamp;
            }

            transition = new(state, boundaryTimestamp);
            changed = state != previousState;
            changedCallback = changed ? TransitionChanged : null;
        }
        // The coordinator also enters this gate while holding its own lock.
        // Notify only after releasing ours, and let it read the latest state
        // because concurrent notifications can finish in a different order.
        changedCallback?.Invoke();
        return true;
    }
}

internal readonly record struct LowPowerTransition(long Generation, long TimestampQpc)
{
    internal bool Suspending => (Generation & 1) != 0;
}
