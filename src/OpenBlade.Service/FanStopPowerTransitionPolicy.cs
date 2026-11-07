using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal enum FanStopPowerTransition { Continue, VerifyOwnedStop, Automatic }

internal static class FanStopPowerTransitionPolicy
{
    // Adapter upgrades hand control back to firmware. A lower-power transition
    // may keep this exact owned Stop only after independent control readback.
    internal static FanStopPowerTransition Evaluate(PowerSource previous, PowerSource current)
    {
        static int Rank(PowerSource source) => source switch
        {
            PowerSource.PluggedIn => 2,
            PowerSource.UsbC => 1,
            PowerSource.Battery => 0,
            _ => -1,
        };
        int before = Rank(previous), after = Rank(current);
        return before < 0 || after < 0 || after > before ? FanStopPowerTransition.Automatic
            : after < before ? FanStopPowerTransition.VerifyOwnedStop : FanStopPowerTransition.Continue;
    }
}
