using OpenBlade.Contracts;

namespace OpenBlade.Service;

public static class OverrideResolver
{
    public static ResolvedSetting<T> Resolve<T>(
        T? profileValue,
        T confirmedValue,
        ManualOverride<T>? temporaryOverride,
        ManualOverride<T>? persistentOverride,
        SettingValueSource profileSource = SettingValueSource.PowerProfile)
    {
        if (persistentOverride is not null)
        {
            return new ResolvedSetting<T>(
                persistentOverride.Value,
                SettingValueSource.PersistentOverride);
        }

        if (temporaryOverride is not null)
        {
            return new ResolvedSetting<T>(
                temporaryOverride.Value,
                SettingValueSource.TemporaryOverride);
        }

        if (profileValue is not null)
        {
            return new ResolvedSetting<T>(profileValue, profileSource);
        }

        return new ResolvedSetting<T>(confirmedValue, SettingValueSource.ConfirmedFirmwareState);
    }
}
