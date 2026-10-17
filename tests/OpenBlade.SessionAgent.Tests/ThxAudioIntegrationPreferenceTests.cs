using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioIntegrationPreferenceTests
{
    [Fact]
    public async Task ExistingPreferenceWatcherPublishesDisabledOnReadFailure()
    {
        int reads = 0;
        var observations = new List<bool>();
        var source = new SpecialKeyBindingSource(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "preferences.json"),
            _ => ++reads == 1
                ? Task.FromResult(UiPreferences.Default with { ThxAudioIntegrationEnabled = true })
                : Task.FromException<UiPreferences>(new IOException("locked")));
        source.PreferencesObserved += preferences =>
            observations.Add(preferences?.ThxAudioIntegrationEnabled == true);

        await source.ReloadIfChangedAsync(force: true, CancellationToken.None);
        await source.ReloadIfChangedAsync(force: true, CancellationToken.None);

        Assert.Equal(new[] { true, false }, observations);
    }
}
