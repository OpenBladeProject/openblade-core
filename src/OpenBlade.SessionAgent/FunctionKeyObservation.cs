using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Knowledge comes only from the admitted model's actual Fn report.</summary>
internal sealed class FunctionKeyObservation
{
    private int state = -1;

    internal bool Known => Read().HasValue;
    internal bool Pressed => Read() == true;

    internal bool? Read() => Volatile.Read(ref state) switch
    {
        0 => false,
        1 => true,
        _ => null,
    };

    internal void Observe(RazerBladeInputDeviceKind deviceKind, ReadOnlySpan<byte> report)
    {
        // These are the same exact models/report geometry admitted by the
        // Fn-layer decoder; ordinary keyboard packets do not establish Fn state.
        if (!RazerBladeFunctionRowMediaInput.SupportsDevice(deviceKind)
            || report.IsEmpty || report[0] != 0x04)
        {
            return;
        }
        if (report.Length < 3)
        {
            Invalidate();
            return;
        }
        Volatile.Write(ref state, report[1] == 0x0A ? 1 : 0);
    }

    internal void Invalidate() => Volatile.Write(ref state, -1);
}
