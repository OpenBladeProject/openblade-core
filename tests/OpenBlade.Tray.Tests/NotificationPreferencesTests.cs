using System.Text.Json;

namespace OpenBlade.Tray.Tests;

public sealed class NotificationPreferencesTests
{
    [Fact]
    public void DefaultsKeepRoutinePowerChangesQuiet()
    {
        NotificationPreferences preferences = NotificationPreferences.Default;

        Assert.True(preferences.PerformanceChangesEnabled);
        Assert.False(preferences.ProfileAndPowerSourceChangesEnabled);
        Assert.True(preferences.AppUpdatesEnabled);
        Assert.True(preferences.FirmwareUpdatesEnabled);
        Assert.True(preferences.ControllerAdvisoriesEnabled);
        Assert.True(preferences.OperationFailuresEnabled);
        Assert.True(preferences.ExternalPowerDischargeEnabled);
    }

    [Theory]
    [InlineData(NotificationCategory.PerformanceChanges)]
    [InlineData(NotificationCategory.ProfileAndPowerSourceChanges)]
    [InlineData(NotificationCategory.AppUpdates)]
    [InlineData(NotificationCategory.FirmwareUpdates)]
    [InlineData(NotificationCategory.ControllerAdvisories)]
    [InlineData(NotificationCategory.OperationFailures)]
    [InlineData(NotificationCategory.ExternalPowerDischarge)]
    public void CategoriesCanBeChangedIndependently(NotificationCategory category)
    {
        NotificationPreferences before = NotificationPreferences.Default;
        NotificationPreferences after = before.WithEnabled(
            category,
            !before.IsEnabled(category));

        foreach (NotificationCategory candidate in Enum.GetValues<NotificationCategory>())
        {
            Assert.Equal(
                candidate == category
                    ? !before.IsEnabled(candidate)
                    : before.IsEnabled(candidate),
                after.IsEnabled(candidate));
        }
    }

    [Fact]
    public void ExistingJsonWithoutDischargePreferenceKeepsWarningEnabled()
    {
        NotificationPreferences? preferences = JsonSerializer.Deserialize<NotificationPreferences>(
            """
            {
              "PerformanceChangesEnabled": false,
              "ProfileAndPowerSourceChangesEnabled": true,
              "AppUpdatesEnabled": false,
              "FirmwareUpdatesEnabled": false,
              "ControllerAdvisoriesEnabled": false,
              "OperationFailuresEnabled": false
            }
            """);

        Assert.NotNull(preferences);
        Assert.False(preferences.PerformanceChangesEnabled);
        Assert.True(preferences.ProfileAndPowerSourceChangesEnabled);
        Assert.False(preferences.AppUpdatesEnabled);
        Assert.False(preferences.FirmwareUpdatesEnabled);
        Assert.False(preferences.ControllerAdvisoriesEnabled);
        Assert.False(preferences.OperationFailuresEnabled);
        Assert.True(preferences.ExternalPowerDischargeEnabled);
    }

    [Fact]
    public void DischargePreferenceRoundTripsWhenDisabled()
    {
        NotificationPreferences expected = NotificationPreferences.Default with
        {
            ExternalPowerDischargeEnabled = false,
        };

        NotificationPreferences? actual = JsonSerializer.Deserialize<NotificationPreferences>(
            JsonSerializer.Serialize(expected));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IdenticalTaggedEventsAreSuppressedWithinTheRepeatWindow()
    {
        var time = new SettableTimeProvider(new DateTimeOffset(
            2026,
            8,
            6,
            12,
            0,
            0,
            TimeSpan.Zero));
        var deduplicator = new NotificationDeduplicator(time);

        Assert.True(deduplicator.ShouldEmit("updates", "Update", "Version 1.2"));
        Assert.False(deduplicator.ShouldEmit("updates", "Update", "Version 1.2"));

        time.Advance(NotificationDeduplicator.DefaultRepeatWindow);

        Assert.True(deduplicator.ShouldEmit("updates", "Update", "Version 1.2"));
    }

    [Fact]
    public void ChangedContentReplacesThePreviousTaggedEventImmediately()
    {
        var deduplicator = new NotificationDeduplicator();

        Assert.True(deduplicator.ShouldEmit("updates", "Update", "Version 1.2"));
        Assert.True(deduplicator.ShouldEmit("updates", "Update", "Version 1.3"));
    }

    [Fact]
    public void NegativeRepeatWindowIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NotificationDeduplicator(repeatWindow: TimeSpan.FromTicks(-1)));
    }

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
