using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

// Product 672: report 4 is a fifteen-slot key-ID set, not an edge packet.
// See docs/blade18-fn-decoder.md for the pinned parser and SDK mappings.
internal sealed class Rz090484FnInputState
{
    private nint collection;
    private bool ambiguous;

    public bool FunctionHeld { get; private set; }

    public bool IsAmbiguous => ambiguous;

    public void Reset()
    {
        collection = 0;
        ambiguous = false;
        FunctionHeld = false;
    }

    public void Invalidate() => FunctionHeld = false;

    public bool IsBoundCollection(nint sourceCollection) =>
        collection != 0 && collection == sourceCollection;

    public void ObserveHid(
        RazerBladeInputDeviceKind deviceKind,
        nint sourceCollection,
        ReadOnlySpan<byte> report,
        bool admitted)
    {
        if (!admitted)
        {
            Invalidate();
            return;
        }
        if (deviceKind != RazerBladeInputDeviceKind.Rz09_0484)
        {
            return;
        }
        if (report.IsEmpty || report.Length != 16 || sourceCollection == 0)
        {
            Invalidate();
            return;
        }
        if (report[0] != 4)
        {
            return;
        }
        if (collection != 0 && collection != sourceCollection)
        {
            ambiguous = true;
        }
        if (ambiguous)
        {
            Invalidate();
            return;
        }
        collection = sourceCollection;
        FunctionHeld = report[1..].ContainsAny((byte)0x0A, (byte)0x0B);
    }
}
