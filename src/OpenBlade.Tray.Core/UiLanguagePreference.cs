namespace OpenBlade.Tray;

public enum UiLanguagePreference
{
    FollowSystem,
    English,
    SimplifiedChinese,
    Russian,
}

public static class UiLanguagePreferencePolicy
{
    public static UiLanguagePreference Normalize(UiLanguagePreference preference) =>
        Enum.IsDefined(preference)
            ? preference
            : UiLanguagePreference.FollowSystem;

    public static string? ResolveCultureName(UiLanguagePreference preference) => preference switch
    {
        UiLanguagePreference.FollowSystem => null,
        UiLanguagePreference.English => "en-US",
        UiLanguagePreference.SimplifiedChinese => "zh-CN",
        UiLanguagePreference.Russian => "ru-RU",
        _ => throw new ArgumentOutOfRangeException(nameof(preference)),
    };
}

public sealed record UiLanguagePreferenceOption(
    UiLanguagePreference Preference,
    string Label);
