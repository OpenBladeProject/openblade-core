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

    public bool FunctionHeld { get; private set; }

    public bool IsAmbiguous => ambiguous;

    public void Reset()
    {
        collection = 0;
        ambiguous = false;
        reportObserved = false;
        FunctionHeld = false;
    }

    public void Invalidate()
    {
        reportObserved = false;
        FunctionHeld = false;
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
