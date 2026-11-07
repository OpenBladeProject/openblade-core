using OpenBlade.Contracts;

namespace OpenBlade.FanStop.Guardian;

internal sealed class So6120ServicePowerReadException(Exception inner)
    : IOException("The adapter read did not establish a resolved native operation.", inner)
{
    internal bool NativeOperationUnresolved => true;
}

internal static class So6120ServicePowerReadPolicy
{
    internal static PowerSource Classify(AdapterPowerState state) => state switch
    {
        AdapterPowerState.Battery => PowerSource.Battery,
        AdapterPowerState.FullPower280W or AdapterPowerState.FullPower330W
            or AdapterPowerState.SufficientExternal => PowerSource.PluggedIn,
        AdapterPowerState.UsbCPower45W or AdapterPowerState.UsbCPower60W or AdapterPowerState.UsbCPower65W
            or AdapterPowerState.UsbCPower80W or AdapterPowerState.UsbCPower90W or AdapterPowerState.UsbCPower100W
            or AdapterPowerState.LimitedExternal => PowerSource.UsbC,
        _ => PowerSource.ExternalUnknown,
    };

    internal static async Task<PowerSource> ReadAsync(Func<CancellationToken, Task<AdapterPowerState>> query,
        CancellationToken token)
    {
        try
        {
            // Unknown from this typed, completed reply ends Stop admission. It
            // does not invent ambiguity in the separate, still-owned EC path.
            return Classify(await query(token).ConfigureAwait(false));
        }
        catch (Exception exception) { throw new So6120ServicePowerReadException(exception); }
    }
}

internal static class So6120ServiceThermalPolicy
{
    internal static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(2);

    internal static bool Admits(int? cpuCelsius, bool gpuInactive, TimeSpan elapsed, bool canceled) =>
        !canceled && elapsed >= TimeSpan.Zero && elapsed <= MaximumSampleAge
        && cpuCelsius is >= 10 and <= 55 && gpuInactive;
}

// This cache grants no command authority. Every actual command still performs
// the independent full admission in ISo6120SessionEvidenceSource.Capture.
internal sealed class So6120ServiceContinuationMonitor(Func<bool> observe, TimeProvider clock)
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(2);
    private long? started;
    private bool admitted;

    internal bool Observe()
    {
        if (started is long previous && clock.GetElapsedTime(previous) < PollInterval) { return admitted; }
        started = clock.GetTimestamp();
        admitted = false;
        try
        {
            bool result = observe();
            admitted = result && clock.GetElapsedTime(started.Value) <= MaximumSampleAge;
        }
        catch { admitted = false; }
        return admitted;
    }
}
