namespace OpenBlade.Tray;

public enum NotificationCategory
{
    PerformanceChanges,
    ProfileAndPowerSourceChanges,
    AppUpdates,
    FirmwareUpdates,
    ControllerAdvisories,
    OperationFailures,
    ExternalPowerDischarge,
}

public sealed record NotificationPreferences(
    bool PerformanceChangesEnabled = true,
    bool ProfileAndPowerSourceChangesEnabled = false,
    bool AppUpdatesEnabled = true,
    bool FirmwareUpdatesEnabled = true,
    bool ControllerAdvisoriesEnabled = true,
    bool OperationFailuresEnabled = true,
    bool ExternalPowerDischargeEnabled = true)
{
    public static NotificationPreferences Default { get; } = new();

    public bool IsEnabled(NotificationCategory category) => category switch
    {
        NotificationCategory.PerformanceChanges => PerformanceChangesEnabled,
        NotificationCategory.ProfileAndPowerSourceChanges =>
            ProfileAndPowerSourceChangesEnabled,
        NotificationCategory.AppUpdates => AppUpdatesEnabled,
        NotificationCategory.FirmwareUpdates => FirmwareUpdatesEnabled,
        NotificationCategory.ControllerAdvisories => ControllerAdvisoriesEnabled,
        NotificationCategory.OperationFailures => OperationFailuresEnabled,
        NotificationCategory.ExternalPowerDischarge => ExternalPowerDischargeEnabled,
        _ => false,
    };

    public NotificationPreferences WithEnabled(
        NotificationCategory category,
        bool enabled) => category switch
        {
            NotificationCategory.PerformanceChanges => this with
            {
                PerformanceChangesEnabled = enabled,
            },
            NotificationCategory.ProfileAndPowerSourceChanges => this with
            {
                ProfileAndPowerSourceChangesEnabled = enabled,
            },
            NotificationCategory.AppUpdates => this with
            {
                AppUpdatesEnabled = enabled,
            },
            NotificationCategory.FirmwareUpdates => this with
            {
                FirmwareUpdatesEnabled = enabled,
            },
            NotificationCategory.ControllerAdvisories => this with
            {
                ControllerAdvisoriesEnabled = enabled,
            },
            NotificationCategory.OperationFailures => this with
            {
                OperationFailuresEnabled = enabled,
            },
            NotificationCategory.ExternalPowerDischarge => this with
            {
                ExternalPowerDischargeEnabled = enabled,
            },
            _ => this,
        };
}

/// <summary>
/// Suppresses repeated tagged notifications on the tray UI thread. Callers keep
/// the replacement-tag set bounded; this type is not thread-safe.
/// </summary>
public sealed class NotificationDeduplicator
{
    public static readonly TimeSpan DefaultRepeatWindow = TimeSpan.FromSeconds(30);

    private readonly TimeProvider timeProvider;
    private readonly TimeSpan repeatWindow;
    private readonly Dictionary<string, Observation> observations =
        new(StringComparer.Ordinal);

    public NotificationDeduplicator(
        TimeProvider? timeProvider = null,
        TimeSpan? repeatWindow = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        TimeSpan selectedRepeatWindow = repeatWindow ?? DefaultRepeatWindow;
        if (selectedRepeatWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(repeatWindow));
        }
        this.repeatWindow = selectedRepeatWindow;
    }

    public bool ShouldEmit(string replacementTag, string title, string body) =>
        ShouldEmit(replacementTag, title, body, occurrenceId: null);

    public bool ShouldEmit(string replacementTag, string title, string body, long? occurrenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementTag);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        DateTimeOffset now = timeProvider.GetUtcNow();
        string fingerprint = string.Concat(title, "\n", body);
        if (observations.TryGetValue(replacementTag, out Observation? previous)
            && string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal)
            && previous.OccurrenceId == occurrenceId
            && now - previous.ObservedAt < repeatWindow)
        {
            return false;
        }

        observations[replacementTag] = new(fingerprint, now, occurrenceId);
        return true;
    }

    private sealed record Observation(string Fingerprint, DateTimeOffset ObservedAt, long? OccurrenceId);
}
