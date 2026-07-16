using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record BatteryAutomationPreferences(bool ProtectionEnabled);

public sealed record BatteryConfiguration(
    ProfileSet Profiles,
    BatteryAutomationPreferences Preferences,
    bool Recovered);

internal sealed record ProfileDocument(int SchemaVersion, ProfileSet Profiles);

internal sealed record BatteryPreferencesDocument(
    int SchemaVersion,
    BatteryAutomationPreferences Preferences);

internal sealed record BatteryConfigurationCommit(
    int SchemaVersion,
    string ProfileSha256,
    string PreferencesSha256);
