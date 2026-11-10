using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>Tracks discharge episodes using fresh, explicitly requested battery readings.</summary>
internal sealed class ExternalPowerDischargeTracker
{
    private bool warnedDuringDischarge;

    public bool IsDischarging { get; private set; }

    public long Episode { get; private set; }

    public void ObserveSource(PowerSource source, bool deviceVerified)
    {
        if (deviceVerified && source == PowerSource.Battery)
        {
            Reset();
        }
        // A temporary unknown source is not confirmed recovery for an active episode.
    }

    public void Observe(int? batteryPowerMilliwatts, bool suppressWarning = false)
    {
        if (batteryPowerMilliwatts is null)
        {
            return;
        }

        if (batteryPowerMilliwatts >= 0)
        {
            Reset();
            return;
        }

        // The charge-limit exception can hide the badge during an ongoing discharge.
        // Rearm the notification only when the discharge actually ends.
        IsDischarging = !suppressWarning;
        if (IsDischarging && !warnedDuringDischarge)
        {
            warnedDuringDischarge = true;
            Episode++;
        }
    }

    public void Reset()
    {
        IsDischarging = false;
        warnedDuringDischarge = false;
    }
}
