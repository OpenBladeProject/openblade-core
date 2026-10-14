using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

// ProtectionEnabled is read only while migrating schema 1-10 configuration.
// Current-schema writes omit it because PowerProfile owns the setting.
public sealed record BatteryAutomationPreferences(
    [property: JsonPropertyName("protectionEnabled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ProtectionEnabled = null,
    bool AlwaysOverrideExternalLightingChanges = false,
    LightingSettings? LastKnownLighting = null,
    KeyboardLightingControlMode KeyboardLightingControlMode =
        KeyboardLightingControlMode.OpenBlade,
    bool AllowUntestedFirmware = false,
    LightingRefreshRate LightingRefreshRate = LightingRefreshRate.ThirtyHz,
    ThermalControlPolicy ThermalControlPolicy =
        ThermalControlPolicy.MaintainDesiredState,
    ThermalPolicyUpgradeDisclosureState ThermalPolicyUpgradeDisclosure =
        ThermalPolicyUpgradeDisclosureState.NotRequired,
    bool AllowExperimentalFeatures = true);

public sealed record BatteryConfiguration(
    ProfileSet Profiles,
    BatteryAutomationPreferences Preferences,
    bool Recovered,
    ConfigurationLoadDisposition Disposition = ConfigurationLoadDisposition.Admitted,
    ConfigurationQuarantineReason QuarantineReason = ConfigurationQuarantineReason.None)
{
    public bool IsAuthoritative => Disposition == ConfigurationLoadDisposition.Admitted;
}

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
