using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Readable configuration from one exact pad; target RPM is not a tachometer reading.</summary>
public sealed record CoolingPadDeviceSnapshot(
    string Identity,
    CoolingPadFanMode? FanMode = null,
    int? TargetRpm = null,
    int? BrightnessPercent = null,
    CoolingPadLightingMode? LightingMode = null,
    CoolingPadSettings? LightingSettings = null)
{
    public CoolingPadFanConfiguration? FanConfiguration { get; init; }
    public CoolingPadLightingConfiguration? LightingConfiguration { get; init; }
    public CoolingPadLightingConfiguration? ActiveLightingConfiguration { get; init; }
    public byte? BrightnessRaw { get; init; }
    public CoolingPadDeviceMode? DeviceMode { get; init; }
}

public interface ICoolingPadDeviceProvider
{
    Task<ICoolingPadDevice?> OpenAsync(CancellationToken cancellationToken);
    CoolingPadConnectionState ObserveConnection() => CoolingPadConnectionState.Unavailable;
}

/// <summary>Service-owned typed access. Every write rechecks admission at native submission.</summary>
public interface ICoolingPadDevice : IAsyncDisposable
{
    string Identity { get; }
    Task<CoolingPadDeviceSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);
    Task SetFanAsync(CoolingPadFanMode mode, int? rpm, Func<bool> submissionAllowed, CancellationToken cancellationToken);
    Task SetLightingAsync(CoolingPadSettings settings, Func<bool> submissionAllowed, CancellationToken cancellationToken);
    Task PushMirrorFrameAsync(IReadOnlyList<CoolingPadColor> colors, Func<bool> submissionAllowed, CancellationToken cancellationToken);
    Task RestoreAsync(CoolingPadDeviceSnapshot baseline, bool restoreFan, bool restoreLighting, Func<bool> submissionAllowed, CancellationToken cancellationToken);
}

public sealed class CoolingPadOperationException(string message, bool submissionUnknown) : IOException(message)
{
    public bool SubmissionUnknown { get; } = submissionUnknown;
    // Only native submission may prove that a transient local gate closed before any SET.
    internal bool LocalAdmissionDeferred { get; init; }
}

public enum CoolingPadFanOwnership { Firmware, Synapse }
public enum CoolingPadFanPreset { Low, Medium, High, Quiet, Balanced, Performance, Off }

/// <summary>The vendor's mode and configured target, preserved independently of application intent.</summary>
public sealed record CoolingPadFanConfiguration(
    CoolingPadFanOwnership Ownership, CoolingPadFanPreset Preset, int ConfiguredRpm);

public enum CoolingPadDeviceMode { Normal, Driver = 3 }
/// <summary>Bounded effect restoration state; it exposes no raw report buffer or byte-reference signature.</summary>
public sealed record CoolingPadLightingConfiguration
{
    public CoolingPadLightingConfiguration(byte profile, byte region, byte effect, byte flags, byte rate,
        IReadOnlyList<CoolingPadColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (profile is not (0 or 1) || region != 0 || effect is not (0 or 1 or 2 or 3 or 4 or 8)
            || colors.Count > 2 || colors.Any(color => color is null))
        { throw new ArgumentOutOfRangeException(nameof(colors), "Invalid cooling-pad effect restoration state."); }
        Profile = profile; Region = region; Effect = effect; Flags = flags; Rate = rate;
        Colors = Array.AsReadOnly(colors.ToArray());
    }

    public byte Profile { get; init; }
    public byte Region { get; init; }
    public byte Effect { get; init; }
    public byte Flags { get; init; }
    public byte Rate { get; init; }
    public IReadOnlyList<CoolingPadColor> Colors { get; init; }
}

/// <summary>Service-only binding of protected original restoration state after explicit read/recovery.</summary>
internal interface ICoolingPadRestorationProvenance
{
    void BindLightingBaseline(CoolingPadDeviceSnapshot baseline);
}
