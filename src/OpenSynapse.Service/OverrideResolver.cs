using OpenSynapse.Contracts;

namespace OpenSynapse.Service;

public static class OverrideResolver
{
    public static ResolvedSetting<T> Resolve<T>(
        T? profileValue,
        T confirmedValue,
        ManualOverride<T>? temporaryOverride,
        ManualOverride<T>? persistentOverride)
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
            return new ResolvedSetting<T>(profileValue, SettingValueSource.PowerProfile);
        }

        return new ResolvedSetting<T>(confirmedValue, SettingValueSource.ConfirmedFirmwareState);
    }
}
