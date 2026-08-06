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
    }

    [Fact]
    public void CategoriesCanBeChangedIndependently()
    {
        NotificationPreferences preferences = NotificationPreferences.Default
            .WithEnabled(NotificationCategory.AppUpdates, enabled: false);

        Assert.False(preferences.AppUpdatesEnabled);
        Assert.True(preferences.FirmwareUpdatesEnabled);
        Assert.True(preferences.OperationFailuresEnabled);
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

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
