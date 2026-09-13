namespace OpenBlade.Tray;

public sealed record ProfileValuePresentation(
    string Text,
    string SettingName,
    string ProfileName,
    bool IsPending = false,
    string PendingText = "")
{
    public string? PendingDescription => IsPending
        ? TrayText.Format("Pending: {0}", PendingText)
        : null;

    public string AccessibilityDescription => IsPending
        ? TrayText.Format(
            "{0} {1}: {2}. Pending: {3}",
            ProfileName,
            SettingName,
            Text,
            PendingText)
        : TrayText.Format("{0} {1}: {2}", ProfileName, SettingName, Text);
}

public sealed record ProfilePresentation(
    ProfileValuePresentation PowerMode,
    ProfileValuePresentation WindowsPowerMode,
    ProfileValuePresentation GpuTuning,
    ProfileValuePresentation Fans,
    ProfileValuePresentation BatteryProtection,
    ProfileValuePresentation LightingEffect,
    ProfileValuePresentation LightingBrightness,
    ProfileValuePresentation LightingColor,
    ProfileValuePresentation LogoLighting,
    ProfileValuePresentation LightingDisplayOff,
    ProfileValuePresentation LightingIdle,
    ProfileValuePresentation DisplayRefreshRate,
    ProfileValuePresentation DisplayHdr,
    ProfileValuePresentation DisplayColorProfile,
    ProfileValuePresentation FunctionKeyBehavior,
    ProfileValuePresentation GamingMode,
    ProfileValuePresentation GamingModeWindowsKey,
    ProfileValuePresentation GamingModeAltTab,
    ProfileValuePresentation StartupAnimation);
