using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Fn state from the admitted model's actual Fn reports.</summary>
internal sealed class FunctionKeyObservation
{
    // The keyboard reports Fn only when it changes, so a new observer cannot
    // see a hold that began earlier. Start released, as the Windows-hook
    // backend does, and treat a source reset like a fresh start so the F-row
    // prompt does not return and a missed release cannot stick. A wrong guess
    // maps only F-row presses made while that hold lasts to the other layer's
    // benign actions, and the release report corrects it. A malformed report
    // or failed packet may have hidden a transition, so it clears the state to
    // unknown until the next report or reset.
    private int state;

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

    internal void AssumeReleased() => Volatile.Write(ref state, 0);

    internal void Invalidate() => Volatile.Write(ref state, -1);
}
