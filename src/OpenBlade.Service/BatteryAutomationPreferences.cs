using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record BatteryAutomationPreferences(
    bool ProtectionEnabled,
    bool AlwaysOverrideExternalLightingChanges = false,
    LightingSettings? LastKnownLighting = null);

public sealed record BatteryConfiguration(
    ProfileSet Profiles,
    BatteryAutomationPreferences Preferences,
    bool Recovered);

internal sealed record ProfileDocument(int SchemaVersion, ProfileSet Profiles);

internal sealed record LegacyProfileDocument(
    int SchemaVersion,
    LegacyProfileSet Profiles);

internal sealed record LegacyProfileSet(
    PowerProfile PluggedIn,
    PowerProfile Battery,
    BatteryPolicy BatteryPolicy);

internal sealed record BatteryPreferencesDocument(
    int SchemaVersion,
    BatteryAutomationPreferences Preferences);

internal sealed record BatteryConfigurationCommit(
    int SchemaVersion,
    string ProfileSha256,
    string PreferencesSha256);
