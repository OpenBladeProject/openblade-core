using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SpecialKeyBindingSourceTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.SessionAgent.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReloadIfChangedAsync_TransientReadFailureRetainsAndRetriesBindings()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "initial");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 13, 1, 0, 0));

        int loads = 0;
        var errors = new List<Exception>();
        var source = new SpecialKeyBindingSource(
            path,
            _ => ++loads switch
            {
                1 => Task.FromResult(PreferencesWith(
                    SpecialKeyActionKind.Disabled)),
                2 => Task.FromException<UiPreferences>(
                    new IOException("The file is locked.")),
                _ => Task.FromResult(PreferencesWith(
                    SpecialKeyActionKind.CyclePowerMode)),
            },
            errors.Add);

        await source.ReloadIfChangedAsync(force: true, CancellationToken.None);
        Assert.Equal(SpecialKeyActionKind.Disabled, source.GetAction(SpecialKeySlot.M1).Kind);

        await File.WriteAllTextAsync(path, "updated and intentionally longer");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 13, 1, 1, 0));

        await source.ReloadIfChangedAsync(force: false, CancellationToken.None);

        Assert.Equal(SpecialKeyActionKind.Disabled, source.GetAction(SpecialKeySlot.M1).Kind);
        Assert.IsType<IOException>(Assert.Single(errors));

        await source.ReloadIfChangedAsync(force: false, CancellationToken.None);

        Assert.Equal(3, loads);
        Assert.Equal(
            SpecialKeyActionKind.CyclePowerMode,
            source.GetAction(SpecialKeySlot.M1).Kind);
    }

    private static UiPreferences PreferencesWith(SpecialKeyActionKind action) =>
        UiPreferences.Default with
        {
            SpecialKeyBindings =
            [
                new(
                    SpecialKeySlot.M1,
                    new SpecialKeyAction(action)),
            ],
        };

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
