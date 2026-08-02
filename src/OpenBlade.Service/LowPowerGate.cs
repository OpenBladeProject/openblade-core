namespace OpenBlade.Service;

public sealed class LowPowerGate
{
    private int active;

    public bool IsActive => Volatile.Read(ref active) != 0;

    public bool Enter() => Interlocked.Exchange(ref active, 1) == 0;

    public bool Exit() => Interlocked.Exchange(ref active, 0) != 0;
}
