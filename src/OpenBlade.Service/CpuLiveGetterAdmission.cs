using System.Diagnostics;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

// One bounded getter owns this proof. It is never a command/write admission and
// cannot be renewed by another getter, even on the same initiating live lease.
internal sealed class CpuLiveGetterAdmission(
    CancellationToken token, Func<bool> lifetime,
    Func<CpuLiveGetterAdmission, bool> admit,
    Func<TimeSpan>? elapsed = null) : IDisposable
{
    private readonly long started = Stopwatch.GetTimestamp();
    private int revoked;
    private bool classified;

    internal bool Allows()
    {
        if (!Active()) { return false; }
        bool accepted;
        try { accepted = admit(this) && Active(); }
        catch { accepted = false; }
        if (!accepted) { Dispose(); }
        return accepted && Volatile.Read(ref revoked) == 0;
    }

    // Called at the existing runtime power-admission position. Classification
    // includes the full adapter consensus; later checks still sample Windows.
    internal bool FullPower(Func<bool> classify, Func<PowerSource> capture)
    {
        if (!Active()) { return false; }
        if (!classified)
        {
            if (!classify() || !Active()) { Dispose(); return false; }
            classified = true;
        }
        bool accepted = capture() is PowerSource.ExternalUnknown or PowerSource.PluggedIn;
        if (!accepted || !Active()) { Dispose(); return false; }
        return true;
    }

    private bool Active()
    {
        if (Volatile.Read(ref revoked) != 0) { return false; }
        if (token.IsCancellationRequested || !lifetime()
            || (elapsed?.Invoke() ?? Stopwatch.GetElapsedTime(started)) >= TimeSpan.FromSeconds(10))
        { Dispose(); return false; }
        return true;
    }

    public void Dispose() => Interlocked.Exchange(ref revoked, 1);
}
