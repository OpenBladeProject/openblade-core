using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>Tracks discharge episodes using fresh, explicitly requested battery readings.</summary>
internal sealed class ExternalPowerDischargeTracker
{
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

    public void Observe(int? batteryPowerMilliwatts)
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

        if (!IsDischarging)
        {
            IsDischarging = true;
            Episode++;
        }
    }

    public void Reset()
    {
        IsDischarging = false;
    }
}
