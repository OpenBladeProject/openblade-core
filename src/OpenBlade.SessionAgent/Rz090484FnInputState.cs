using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal enum Blade18FnReportObservation
{
    None,
    FirstReleased,
    Pressed,
    Released,
}

// Product 672: report 4 is a fifteen-slot key-ID set, not an edge packet.
// See docs/blade18-fn-decoder.md for the pinned parser and SDK mappings.
internal sealed class Rz090484FnInputState
{
    private nint collection;
    private bool ambiguous;
    private bool reportObserved;
    private sealed record Observation(nint Collection, bool Pressed);
    private Observation? observation;

    internal void InvalidateKnowledge() => Volatile.Write(ref observation, null);

    internal bool? Read() => Volatile.Read(ref observation)?.Pressed;

    internal bool? ReadAdmitted(Func<nint, bool> isCurrent)
    {
        Observation? current = Volatile.Read(ref observation);
        if (current is null)
        {
            return null;
        }
        // Native admission can race a topology reset or a newer Fn report.
        // Apply its result only to the observation it actually checked.
        if (!isCurrent(current.Collection))
        {
            Interlocked.CompareExchange(ref observation, null, current);
            return null;
        }
        return ReferenceEquals(current, Volatile.Read(ref observation)) ? current.Pressed : null;
    }

    public bool FunctionHeld { get; private set; }

    public bool IsAmbiguous => ambiguous;

    public void Reset()
    {
        collection = 0;
        ambiguous = false;
        reportObserved = false;
        FunctionHeld = false;
        InvalidateKnowledge();
    }

    public void Invalidate()
    {
        reportObserved = false;
        FunctionHeld = false;
        InvalidateKnowledge();
    }

    public bool IsBoundCollection(nint sourceCollection) =>
        collection != 0 && collection == sourceCollection;

    public Blade18FnReportObservation ObserveHid(
        RazerBladeInputDeviceKind deviceKind,
        nint sourceCollection,
        ReadOnlySpan<byte> report,
        bool admitted)
    {
        if (!admitted)
        {
            Invalidate();
            return Blade18FnReportObservation.None;
        }
        if (deviceKind != RazerBladeInputDeviceKind.Rz09_0484)
        {
            return Blade18FnReportObservation.None;
        }
        if (report.IsEmpty || report.Length != 16 || sourceCollection == 0)
        {
            Invalidate();
            return Blade18FnReportObservation.None;
        }
        if (report[0] != 4)
        {
            return Blade18FnReportObservation.None;
        }
        if (collection != 0 && collection != sourceCollection)
        {
            ambiguous = true;
        }
        if (ambiguous)
        {
            Invalidate();
            return Blade18FnReportObservation.None;
        }
        collection = sourceCollection;
        bool wasObserved = reportObserved;
        bool wasHeld = FunctionHeld;
        FunctionHeld = report[1..].ContainsAny((byte)0x0A, (byte)0x0B);
        reportObserved = true;
        Volatile.Write(ref observation, new(collection, FunctionHeld));
        if (!wasObserved)
        {
            return FunctionHeld
                ? Blade18FnReportObservation.Pressed
                : Blade18FnReportObservation.FirstReleased;
        }
        if (wasHeld == FunctionHeld)
        {
            return Blade18FnReportObservation.None;
        }
        return FunctionHeld
            ? Blade18FnReportObservation.Pressed
            : Blade18FnReportObservation.Released;
    }
}
