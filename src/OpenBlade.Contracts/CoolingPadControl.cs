namespace OpenBlade.Contracts;

public enum CoolingPadTemperatureSource { Cpu, Gpu, Both }
public enum CoolingPadFanMode { Keep, Off, Fixed, HostCurve, Auto }
/// <summary>Synapse Smart profiles. Each selects firmware preset Quiet, Balanced or Performance.</summary>
public enum CoolingPadAutoProfile { Quiet, Balanced, Performance }
public enum CoolingPadLightingMode { Keep, Off, Static, Breathing, Spectrum, Wave, KeyboardMirror }
public enum CoolingPadWaveDirection { Left = 1, Right = 2 }
public sealed record CoolingPadColor(byte R, byte G, byte B);
public sealed record CoolingPadCurvePoint(int TemperatureCelsius, int Rpm);

/// <summary>Saved intent. Loading this document never starts pad control.</summary>
public sealed record CoolingPadSettings
{
    public const int MinimumRpm = 500;
    public const int MaximumRpm = 3200;
    public const int RpmStep = 50;
    public const int MaximumCurvePoints = 16;
    public static CoolingPadSettings Default { get; } = new();
    public CoolingPadFanMode FanMode { get; init; }
    public int FixedRpm { get; init; } = 1500;
    public bool AllowDemandApi { get; init; }
    public CoolingPadTemperatureSource TemperatureSource { get; init; } = CoolingPadTemperatureSource.Both;
    public CoolingPadAutoProfile AutoProfile { get; init; } = CoolingPadAutoProfile.Balanced;
    public IReadOnlyList<CoolingPadCurvePoint> CpuCurve { get; init; } = DefaultCurve();
    public IReadOnlyList<CoolingPadCurvePoint> GpuCurve { get; init; } = DefaultCurve();
    public CoolingPadLightingMode LightingMode { get; init; }
    public int BrightnessPercent { get; init; } = 100;
    public CoolingPadColor Color { get; init; } = new(0, 255, 0);
    public CoolingPadColor? SecondaryColor { get; init; }
    public CoolingPadWaveDirection WaveDirection { get; init; } = CoolingPadWaveDirection.Right;

    public static bool IsWellFormed(CoolingPadSettings? value) => value is not null
        && Enum.IsDefined(value.TemperatureSource) && Enum.IsDefined(value.FanMode) && Enum.IsDefined(value.LightingMode)
        && Enum.IsDefined(value.WaveDirection) && Enum.IsDefined(value.AutoProfile) && ValidRpm(value.FixedRpm)
        && value.BrightnessPercent is >= 0 and <= 100 && value.Color is not null
        && ValidCurve(value.CpuCurve) && ValidCurve(value.GpuCurve);

    public static bool ValidRpm(int rpm) => rpm is >= MinimumRpm and <= MaximumRpm && rpm % RpmStep == 0;

    private static bool ValidCurve(IReadOnlyList<CoolingPadCurvePoint>? points)
    {
        if (points is null || points.Count is < 2 or > MaximumCurvePoints) { return false; }
        int temperature = -1, rpm = -1;
        foreach (CoolingPadCurvePoint point in points)
        {
            if (point is null || point.TemperatureCelsius is < 0 or > 110
                || point.TemperatureCelsius <= temperature || point.Rpm < rpm || !ValidRpm(point.Rpm))
            { return false; }
            temperature = point.TemperatureCelsius;
            rpm = point.Rpm;
        }
        return true;
    }

    private static IReadOnlyList<CoolingPadCurvePoint> DefaultCurve() =>
        [new(40, 500), new(50, 500), new(60, 1150), new(70, 1850), new(80, 2500), new(90, 3200)];
}

public sealed record CoolingPadStatusSnapshot
{
    public bool Connected { get; init; }
    public CoolingPadConnectionState Connection { get; init; } = CoolingPadConnectionState.Unavailable;
    public string? Identity { get; init; }
    public CoolingPadSettings Requested { get; init; } = CoolingPadSettings.Default;
    public bool Active { get; init; }
    public bool WritesPaused { get; init; }
    public bool RecoveryRequired { get; init; }
    public CoolingPadFanMode? ConfirmedFanMode { get; init; }
    public int? ConfirmedTargetRpm { get; init; }
    // This backend does not expose a validated measured-speed reading. Never populate this with target RPM.
    public int? MeasuredRpm { get; init; }
    public int? ConfirmedBrightnessPercent { get; init; }
    public CoolingPadLightingMode? ConfirmedLightingMode { get; init; }
    // True while lighting changed on the pad is left in place and OpenBlade controls only the fan.
    public bool LightingKept { get; init; }
    public string Message { get; init; } = "Cooling pad control is stopped.";
    public string Source { get; init; } = "Razer RC21-0230 source-backed experimental";
    public bool Experimental => true;

    public static bool IsWellFormed(CoolingPadStatusSnapshot? value) => value is not null
        && CoolingPadSettings.IsWellFormed(value.Requested) && Enum.IsDefined(value.Connection)
        && (value.Connection != CoolingPadConnectionState.PresentExact || value.Connected)
        && (value.Connection != CoolingPadConnectionState.Absent || !value.Connected)
        && (!value.Active || value.Connected && !value.WritesPaused)
        && (value.ConfirmedFanMode is null || Enum.IsDefined(value.ConfirmedFanMode.Value))
        && (value.ConfirmedLightingMode is null || Enum.IsDefined(value.ConfirmedLightingMode.Value))
        && (value.ConfirmedTargetRpm is null || value.ConfirmedTargetRpm is >= 0 and <= 3200 && value.ConfirmedTargetRpm % 50 == 0)
        && value.MeasuredRpm is null && value.ConfirmedBrightnessPercent is not (< 0 or > 100)
        && value.Identity is not { Length: > 256 }
        && value.Message is { Length: <= 2048 } && value.Source is { Length: > 0 and <= 2048 };
}
