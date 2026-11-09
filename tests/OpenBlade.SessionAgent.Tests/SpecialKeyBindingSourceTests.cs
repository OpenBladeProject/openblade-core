using System.Threading.Channels;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SpecialKeyBindingSourceTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.SessionAgent.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RealInputPreferences_IgnoreUiEnumsAndRetainConfirmedStateOnMalformedInput()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        const string json = """
            {"primaryNavigationItems":["ThxAudio"],"thxAudioIntegrationEnabled":true,
             "specialKeyBindings":[{"slot":"M1","action":{"kind":"Disabled"}}],
             "snapTapEnabled":true}
            """;
        await File.WriteAllTextAsync(path, json);
        var errors = new List<Exception>();
        var transitions = new List<bool>();
        var source = new SpecialKeyBindingSource(path, errors.Add);
        source.SnapTapEnabledChanged += transitions.Add;

        Assert.True(await source.ReloadIfChangedAsync(force: true, CancellationToken.None));
        Assert.True(source.SnapTapEnabled);
        Assert.Equal(SpecialKeyActionKind.Disabled, source.GetAction(SpecialKeySlot.M1).Kind);

        await File.WriteAllTextAsync(path, "{\"snapTapEnabled\":\"yes\"}");
        Assert.False(await source.ReloadIfChangedAsync(force: true, CancellationToken.None));
        Assert.True(source.SnapTapEnabled);
        Assert.Equal(SpecialKeyActionKind.Disabled, source.GetAction(SpecialKeySlot.M1).Kind);
        Assert.Single(errors);
        Assert.Equal([true], transitions);

        await File.WriteAllTextAsync(path, json.Replace("true}", "false}"));
        Assert.True(await source.ReloadIfChangedAsync(force: true, CancellationToken.None));
        Assert.False(source.SnapTapEnabled);
        Assert.Equal([true, false], transitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealInputPreferences_PreserveThxOptInForSharedWatcher(bool enabled)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path,
            "{\"primaryNavigationItems\":[\"FuturePage\"],\"snapTapEnabled\":true,"
            + "\"thxAudioIntegrationEnabled\":" + (enabled ? "true" : "false") + "}");
        var source = new SpecialKeyBindingSource(path);
        UiPreferences? observed = null;
        source.PreferencesObserved += preferences => observed = preferences;

        Assert.True(await source.ReloadIfChangedAsync(force: true, CancellationToken.None));
        Assert.True(source.SnapTapEnabled);
        Assert.NotNull(observed);
        Assert.Equal(enabled, observed.ThxAudioIntegrationEnabled);
    }

    [Fact]
    public async Task SnapTapPreference_EmitsOnlyConfirmedTransitionsAndRetainsOnReadFailure()
    {
        bool enabled = true;
        bool failRead = false;
        var transitions = new List<bool>();
        var source = new SpecialKeyBindingSource(
            Path.Combine(directory, "preferences.json"),
            _ => failRead
                ? Task.FromException<UiPreferences>(new IOException("temporarily unavailable"))
                : Task.FromResult(UiPreferences.Default with { SnapTapEnabled = enabled }));
        source.SnapTapEnabledChanged += transitions.Add;
        Assert.False(source.SnapTapEnabled);

        await source.ReloadIfChangedAsync(force: true, CancellationToken.None);
        await source.ReloadIfChangedAsync(force: true, CancellationToken.None);
        Assert.True(source.SnapTapEnabled);
        Assert.Equal([true], transitions);

        failRead = true;
        await source.ReloadIfChangedAsync(force: true, CancellationToken.None);
        Assert.True(source.SnapTapEnabled);
        Assert.Equal([true], transitions);

        failRead = false;
        enabled = false;
        await source.ReloadIfChangedAsync(force: true, CancellationToken.None);
        Assert.False(source.SnapTapEnabled);
        Assert.Equal([true, false], transitions);
    }

    [Fact]
    public async Task BackendSelectionsRemainIndependentAndOnlyConfirmedChangesArePublished()
    {
        UiPreferences preferences = UiPreferences.Default with
        {
            SnapTapEnabled = true,
            SnapTapBackend = KeyboardInputBackend.WindowsHook,
            FunctionRowBackend = KeyboardInputBackend.RazerDriver,
        };
        bool failRead = false;
        var transitions = new List<KeyboardInputBackend>();
        var source = new SpecialKeyBindingSource(Path.Combine(directory, "preferences.json"),
            _ => failRead ? Task.FromException<UiPreferences>(new IOException("unavailable")) : Task.FromResult(preferences));
        source.SnapTapBackendChanged += transitions.Add;
        Assert.True(await source.ReloadIfChangedAsync(true, CancellationToken.None));
        Assert.Equal(KeyboardInputBackend.WindowsHook, source.SnapTapBackend);
        Assert.Equal(KeyboardInputBackend.RazerDriver, source.FunctionRowBackend);
        failRead = true;
        Assert.False(await source.ReloadIfChangedAsync(true, CancellationToken.None));
        Assert.Equal(KeyboardInputBackend.WindowsHook, source.SnapTapBackend);
        Assert.Equal(KeyboardInputBackend.RazerDriver, source.FunctionRowBackend);
        failRead = false;
        preferences = preferences with { SnapTapBackend = (KeyboardInputBackend)99, FunctionRowBackend = KeyboardInputBackend.WindowsHook };
        Assert.True(await source.ReloadIfChangedAsync(true, CancellationToken.None));
        Assert.True(source.SnapTapEnabled);
        Assert.Equal(KeyboardInputBackend.Unknown, source.SnapTapBackend);
        Assert.Equal(KeyboardInputBackend.WindowsHook, source.FunctionRowBackend);
        Assert.Equal([KeyboardInputBackend.WindowsHook, KeyboardInputBackend.Unknown], transitions);
    }

    [Fact]
    public async Task RealInputPreferencesCarryBothBackendChoicesThroughNarrowLoader()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, """
            {"snapTapBackend":"WindowsHook","functionRowBackend":"RazerDriver"}
            """);
        var source = new SpecialKeyBindingSource(path);
        Assert.True(await source.ReloadIfChangedAsync(true, CancellationToken.None));
        Assert.Equal(KeyboardInputBackend.WindowsHook, source.SnapTapBackend);
        Assert.Equal(KeyboardInputBackend.RazerDriver, source.FunctionRowBackend);
    }

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

    [Fact]
    public async Task RunAsync_ReloadsWhenTheWatcherSignalsAChange()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "initial");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 13, 1, 0, 0));

        int loads = 0;
        int watchers = 0;
        Action? changed = null;
        var delays = new List<TimeSpan>();
        var source = new SpecialKeyBindingSource(
            path,
            _ => Task.FromResult(PreferencesWith(
                ++loads == 1
                    ? SpecialKeyActionKind.Disabled
                    : SpecialKeyActionKind.CyclePowerMode)),
            createWatcher: (_, onChanged, _) =>
            {
                watchers++;
                changed = onChanged;
                return new FakeWatcher();
            },
            delay: (interval, _) =>
            {
                delays.Add(interval);
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = source.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => changed is not null, cancellation.Token);
        Assert.Equal(SpecialKeyActionKind.Disabled, source.GetAction(SpecialKeySlot.M1).Kind);
        Assert.Equal(1, loads);

        changed!();
        await Task.Delay(50, cancellation.Token);
        Assert.Equal(1, loads);

        await File.WriteAllTextAsync(path, "updated and intentionally longer");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 13, 1, 1, 0));
        changed();
        await WaitUntilAsync(
            () => source.GetAction(SpecialKeySlot.M1).Kind == SpecialKeyActionKind.CyclePowerMode,
            cancellation.Token);

        Assert.Equal(
            SpecialKeyActionKind.CyclePowerMode,
            source.GetAction(SpecialKeySlot.M1).Kind);
        Assert.Equal(1, watchers);
        Assert.DoesNotContain(SpecialKeyBindingSource.FallbackRefreshInterval, delays);
        Assert.Contains(SpecialKeyBindingSource.ChangeSettleDelay, delays);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RunAsync_PollsOnlyWhileNoWatcherCanBeArmed()
    {
        string path = Path.Combine(directory, "preferences.json");
        int loads = 0;
        int watcherAttempts = 0;
        Action? changed = null;
        var delays = new List<TimeSpan>();
        var source = new SpecialKeyBindingSource(
            path,
            _ => Task.FromResult(PreferencesWith(
                ++loads == 1
                    ? SpecialKeyActionKind.Disabled
                    : SpecialKeyActionKind.CyclePowerMode)),
            createWatcher: (_, onChanged, _) =>
            {
                if (++watcherAttempts < 3)
                {
                    return null;
                }

                changed = onChanged;
                return new FakeWatcher();
            },
            delay: (interval, _) =>
            {
                delays.Add(interval);
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = source.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => changed is not null, cancellation.Token);

        Assert.Equal(3, watcherAttempts);
        Assert.Equal(
            [
                SpecialKeyBindingSource.FallbackRefreshInterval,
                SpecialKeyBindingSource.FallbackRefreshInterval,
            ],
            delays);
        Assert.Equal(1, loads);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task RunAsync_RearmsTheWatcherAfterItFails()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "initial");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 13, 1, 0, 0));

        int loads = 0;
        var watchers = new List<FakeWatcher>();
        var callbacks = new List<(Action Changed, Action Failed)>();
        var delays = new List<TimeSpan>();
        var source = new SpecialKeyBindingSource(
            path,
            _ => Task.FromResult(PreferencesWith(
                ++loads == 1
                    ? SpecialKeyActionKind.Disabled
                    : SpecialKeyActionKind.CyclePowerMode)),
            createWatcher: (_, onChanged, onFailed) =>
            {
                var watcher = new FakeWatcher();
                watchers.Add(watcher);
                callbacks.Add((onChanged, onFailed));
                return watcher;
            },
            delay: (interval, _) =>
            {
                delays.Add(interval);
                return Task.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = source.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => callbacks.Count == 1, cancellation.Token);
        callbacks[0].Failed();
        await WaitUntilAsync(() => callbacks.Count == 2, cancellation.Token);

        Assert.True(watchers[0].Disposed);
        Assert.False(watchers[1].Disposed);
        Assert.Contains(SpecialKeyBindingSource.WatcherRearmDelay, delays);

        await File.WriteAllTextAsync(path, "updated and intentionally longer");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 13, 1, 1, 0));
        callbacks[1].Changed();
        await WaitUntilAsync(
            () => source.GetAction(SpecialKeySlot.M1).Kind == SpecialKeyActionKind.CyclePowerMode,
            cancellation.Token);

        Assert.Equal(
            SpecialKeyActionKind.CyclePowerMode,
            source.GetAction(SpecialKeySlot.M1).Kind);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(watchers[1].Disposed);
    }

    [Theory]
    [InlineData("preferences.json", null, true)]
    [InlineData("PREFERENCES.JSON", null, true)]
    [InlineData("preferences.json.1a2b.tmp", "preferences.json", true)]
    [InlineData("preferences.json.1a2b.tmp", null, false)]
    [InlineData("trace.log", null, false)]
    [InlineData(null, null, false)]
    public void IsPreferencesFileEvent_MatchesOnlyThePreferencesFileName(
        string? name,
        string? oldName,
        bool expected)
    {
        Assert.Equal(
            expected,
            SpecialKeyBindingSource.IsPreferencesFileEvent("preferences.json", name, oldName));
    }

    [Fact]
    public async Task RunAsync_RetriesFailedReadWithoutAnotherWatcherEventAndStopsRetryingAfterSuccess()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "initial");
        Action? changed = null;
        int loads = 0;
        int retries = 0;
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SpecialKeyBindingSource(
            path,
            _ => ++loads switch
            {
                1 => Task.FromResult(PreferencesWith(SpecialKeyActionKind.Disabled)),
                2 => Task.FromException<UiPreferences>(new IOException("temporarily locked")),
                _ => Task.FromResult(PreferencesWith(SpecialKeyActionKind.CyclePowerMode)),
            },
            createWatcher: (_, onChanged, _) =>
            {
                changed = onChanged;
                return new FakeWatcher();
            },
            delay: (interval, token) =>
            {
                if (interval != SpecialKeyBindingSource.FallbackRefreshInterval)
                {
                    return Task.CompletedTask;
                }
                Interlocked.Increment(ref retries);
                retryStarted.TrySetResult();
                return releaseRetry.Task.WaitAsync(token);
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = source.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(() => changed is not null, cancellation.Token);
            await File.WriteAllTextAsync(path, "updated and longer");
            changed!();
            await retryStarted.Task.WaitAsync(cancellation.Token);
            Assert.Equal(SpecialKeyActionKind.Disabled, source.GetAction(SpecialKeySlot.M1).Kind);
            Assert.Equal(2, loads);
            releaseRetry.TrySetResult();
            await WaitUntilAsync(() => loads == 3, cancellation.Token);
            await WaitUntilAsync(
                () => source.GetAction(SpecialKeySlot.M1).Kind == SpecialKeyActionKind.CyclePowerMode,
                cancellation.Token);
            Assert.Equal(1, retries);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    public async Task RunAsync_CancelsPendingReadRetry()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "initial");
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SpecialKeyBindingSource(
            path,
            _ => Task.FromException<UiPreferences>(new IOException("locked")),
            createWatcher: (_, _, _) => new FakeWatcher(),
            delay: (interval, token) =>
            {
                Assert.Equal(SpecialKeyBindingSource.FallbackRefreshInterval, interval);
                retryStarted.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = source.RunAsync(cancellation.Token);
        await retryStarted.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_RearmsAfterFailureWhileArmingEvenIfChangeOverwritesSignal(bool changeAfterFailure)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "initial");
        var firstWatcher = new FakeWatcher();
        int attempts = 0;
        int loads = 0;
        Action? changed = null;
        var source = new SpecialKeyBindingSource(
            path,
            _ => Task.FromResult(PreferencesWith(++loads == 1
                ? SpecialKeyActionKind.Disabled : SpecialKeyActionKind.CyclePowerMode)),
            createWatcher: (_, onChanged, onFailed) =>
            {
                if (++attempts == 1)
                {
                    onFailed();
                    if (changeAfterFailure)
                    {
                        onChanged();
                    }
                    return firstWatcher;
                }
                changed = onChanged;
                return new FakeWatcher();
            },
            delay: (_, _) => Task.CompletedTask);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = source.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(() => changed is not null, cancellation.Token);
            Assert.Equal(2, attempts);
            Assert.True(firstWatcher.Disposed);
            await File.WriteAllTextAsync(path, "updated and longer");
            changed!();
            await WaitUntilAsync(
                () => source.GetAction(SpecialKeySlot.M1).Kind == SpecialKeyActionKind.CyclePowerMode,
                cancellation.Token);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_PersistentReadFailureBacksOffAndReportsOnce(bool hasWatcher)
    {
        var waits = Channel.CreateUnbounded<(TimeSpan Interval, TaskCompletionSource Release)>();
        int errors = 0;
        var source = new SpecialKeyBindingSource(
            Path.Combine(directory, "preferences.json"),
            _ => Task.FromException<UiPreferences>(new IOException("locked")),
            _ => Interlocked.Increment(ref errors),
            createWatcher: (_, _, _) => hasWatcher ? new FakeWatcher() : null,
            delay: (interval, token) =>
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waits.Writer.TryWrite((interval, release));
                return release.Task.WaitAsync(token);
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = source.RunAsync(cancellation.Token);
        try
        {
            int[] expectedSeconds = [1, 2, 4, 8, 16, 30, 30];
            for (int index = 0; index < expectedSeconds.Length; index++)
            {
                var wait = await waits.Reader.ReadAsync(cancellation.Token);
                Assert.Equal(TimeSpan.FromSeconds(expectedSeconds[index]), wait.Interval);
                Assert.Equal(1, Volatile.Read(ref errors));
                if (index < expectedSeconds.Length - 1)
                {
                    wait.Release.SetResult();
                }
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    public async Task RunAsync_SuccessResetsReadBackoffAndWarningSuppression()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "initial");
        var waits = Channel.CreateUnbounded<(TimeSpan Interval, TaskCompletionSource Release)>();
        int failRead = 1;
        int errors = 0;
        Action? changed = null;
        var source = new SpecialKeyBindingSource(
            path,
            _ => Volatile.Read(ref failRead) != 0
                ? Task.FromException<UiPreferences>(new IOException("locked"))
                : Task.FromResult(PreferencesWith(SpecialKeyActionKind.Disabled)),
            _ => Interlocked.Increment(ref errors),
            createWatcher: (_, onChanged, _) =>
            {
                changed = onChanged;
                return new FakeWatcher();
            },
            delay: (interval, token) =>
            {
                if (interval == SpecialKeyBindingSource.ChangeSettleDelay)
                {
                    return Task.CompletedTask;
                }
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waits.Writer.TryWrite((interval, release));
                return release.Task.WaitAsync(token);
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = source.RunAsync(cancellation.Token);
        try
        {
            var first = await waits.Reader.ReadAsync(cancellation.Token);
            Assert.Equal(TimeSpan.FromSeconds(1), first.Interval);
            first.Release.SetResult();
            var second = await waits.Reader.ReadAsync(cancellation.Token);
            Assert.Equal(TimeSpan.FromSeconds(2), second.Interval);
            Volatile.Write(ref failRead, 0);
            second.Release.SetResult();
            await WaitUntilAsync(
                () => source.GetAction(SpecialKeySlot.M1).Kind == SpecialKeyActionKind.Disabled,
                cancellation.Token);
            Assert.Equal(1, Volatile.Read(ref errors));
            Assert.False(waits.Reader.TryRead(out _));

            Volatile.Write(ref failRead, 1);
            await File.WriteAllTextAsync(path, "updated and longer");
            changed!();
            var restarted = await waits.Reader.ReadAsync(cancellation.Token);
            Assert.Equal(TimeSpan.FromSeconds(1), restarted.Interval);
            Assert.Equal(2, Volatile.Read(ref errors));
            Assert.Equal(SpecialKeyActionKind.Disabled, source.GetAction(SpecialKeySlot.M1).Kind);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    private sealed class FakeWatcher : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task RunAsync_DefaultWatcherObservesAtomicPreferenceReplacement()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "initial");

        int loads = 0;
        var source = new SpecialKeyBindingSource(
            path,
            _ => Task.FromResult(PreferencesWith(
                ++loads == 1
                    ? SpecialKeyActionKind.Disabled
                    : SpecialKeyActionKind.CyclePowerMode)),
            delay: (interval, token) => interval == SpecialKeyBindingSource.FallbackRefreshInterval
                ? throw new InvalidOperationException("The watcher fallback poll was used.")
                : Task.Delay(interval, token));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = source.RunAsync(cancellation.Token);

        await WaitUntilAsync(() => loads == 1, cancellation.Token);
        await Task.Delay(200, cancellation.Token);

        // Mirror the tray store: write a sibling temporary file, then replace.
        string temporaryPath = Path.Combine(directory, "preferences.json.abc123.tmp");
        await File.WriteAllTextAsync(temporaryPath, "updated and intentionally longer");
        File.Move(temporaryPath, path, overwrite: true);

        await WaitUntilAsync(
            () => source.GetAction(SpecialKeySlot.M1).Kind == SpecialKeyActionKind.CyclePowerMode,
            cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
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
