namespace OpenSynapse.Contracts;

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

public sealed record ResolvedSetting<T>(T Value, SettingValueSource Source);
