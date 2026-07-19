namespace OpenBlade.Contracts;

public enum OverrideLifetime
{
    UntilPowerSourceChange,
    Persistent,
}

public enum SettingValueSource
{
    ConfirmedFirmwareState,
    PowerProfile,
    TemporaryOverride,
    PersistentOverride,
}

public sealed record ManualOverride<T>(T Value, OverrideLifetime Lifetime);

public sealed record PerformanceOverride(
    PerformancePreset Value,
    OverrideLifetime Lifetime,
    CustomPerformanceSettings? CustomPerformance = null)
{
    public bool IsWellFormed() =>
        Enum.IsDefined(Value)
        && Enum.IsDefined(Lifetime)
        && (Value == PerformancePreset.Custom
            ? CustomPerformanceSettings.IsWellFormed(CustomPerformance)
            : CustomPerformance is null);

    public static implicit operator PerformanceOverride?(
        ManualOverride<PerformancePreset>? value) => value is null
            ? null
            : new PerformanceOverride(value.Value, value.Lifetime);
}

public sealed record ResolvedSetting<T>(T Value, SettingValueSource Source);
