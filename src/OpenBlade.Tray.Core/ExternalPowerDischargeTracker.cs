using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>Tracks sustained discharge using fresh, explicitly requested battery readings.</summary>
internal sealed class ExternalPowerDischargeTracker
{
    internal static readonly TimeSpan ConfirmationInterval = TimeSpan.FromSeconds(30);
    private DateTimeOffset? dischargeStartedAt;
    private DateTimeOffset? lastSampleAt;

    public bool IsDischarging { get; private set; }

    public long Episode { get; private set; }

    public void ObserveSource(PowerSource source, bool deviceVerified)
    {
        if (deviceVerified && source == PowerSource.Battery)
        {
            Reset();
        }
        else if (!deviceVerified || source is not (PowerSource.PluggedIn or PowerSource.UsbC))
        {
            // A temporary unknown source is neither fresh discharge nor confirmed recovery.
            dischargeStartedAt = null;
            lastSampleAt = null;
        }
    }

    public void Observe(
        DateTimeOffset observedAt,
        int? batteryPowerMilliwatts,
        TimeSpan maximumSampleGap)
    {
        if (batteryPowerMilliwatts is null)
        {
            dischargeStartedAt = null;
            lastSampleAt = null;
            return;
        }

        if (lastSampleAt is { } previous
            && (observedAt < previous || observedAt - previous > maximumSampleGap))
        {
            dischargeStartedAt = null;
        }
        lastSampleAt = observedAt;

        if (batteryPowerMilliwatts >= 0)
        {
            Reset();
            return;
        }

        dischargeStartedAt ??= observedAt;
        if (!IsDischarging && observedAt - dischargeStartedAt >= ConfirmationInterval)
        {
            IsDischarging = true;
            Episode++;
        }
    }

    public void Reset()
    {
        IsDischarging = false;
        dischargeStartedAt = null;
        lastSampleAt = null;
    }
}
