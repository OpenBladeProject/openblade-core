using System.Diagnostics;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// One bounded getter owns this proof. It is never a command/write admission and
// cannot be renewed by another getter, even on the same initiating live lease.
internal sealed class CpuLiveGetterAdmission(
    CancellationToken token, Func<bool> lifetime,
    Func<CpuLiveGetterAdmission, bool> admit,
    Func<TimeSpan>? elapsed = null, Func<CpuLiveGetterAdmission, bool>? continuity = null,
    CpuSavedAdmissionDiagnostic? diagnostic = null, TimeSpan? maximumLifetime = null) : IDisposable
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly TimeSpan deadline = ValidateMaximumLifetime(maximumLifetime);
    private int revoked;
    private bool classified, admitted;
    internal IBladeDevice? Device { get; private set; }
    private static TimeSpan ValidateMaximumLifetime(TimeSpan? maximumLifetime)
    {
        TimeSpan value = maximumLifetime ?? TimeSpan.FromSeconds(10);
        if (value <= TimeSpan.Zero || value > TimeSpan.FromSeconds(30))
        { throw new ArgumentOutOfRangeException(nameof(maximumLifetime)); }
        return value;
    }
    internal bool BindDevice(IBladeDevice device)
    {
        if (Device is null) { Device = device; }
        return ReferenceEquals(Device, device);
    }

    internal bool Revalidate()
    {
        classified = false; admitted = false;
        return Allows();
    }

    internal bool Allows()
    {
        if (!Active()) { return false; }
        bool accepted;
        string stage = admitted && continuity is not null ? "getter continuity admission" : "getter full admission";
        try
        {
            accepted = (admitted && continuity is not null ? continuity(this) : admit(this)) && Active();
            if (accepted) { admitted = true; }
        }
        catch { diagnostic?.Check(false, stage + " exception"); accepted = false; }
        diagnostic?.Check(accepted, stage);
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
        string stage = "getter revoked";
        bool Check(bool allowed, string name) => diagnostic?.Check(allowed, name) ?? allowed;
        try
        {
            if (!Check(Volatile.Read(ref revoked) == 0, stage)) { return false; }
            stage = "getter cancellation";
            if (!Check(!token.IsCancellationRequested, stage)) { Dispose(); return false; }
            stage = "getter lifetime";
            if (!Check(lifetime(), stage)) { Dispose(); return false; }
            stage = "getter deadline";
            TimeSpan measured = elapsed?.Invoke() ?? Stopwatch.GetElapsedTime(started);
            if (!Check(measured < deadline,
                $"{stage}; elapsed {measured.TotalMilliseconds:F0} ms")) { Dispose(); return false; }
            return true;
        }
        catch
        {
            diagnostic?.Check(false, stage + " exception");
            Dispose(); return false;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref revoked, 1);
}
