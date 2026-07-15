namespace OpenBlade.Tray.Tests;

public sealed class UiPreferenceSaveQueueTests
{
    [Fact]
    public async Task Enqueue_SerializesSavesInInvocationOrder()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<ThemePreference>();
        var queue = new UiPreferenceSaveQueue(
            async (preferences, _) =>
            {
                if (preferences.Theme == ThemePreference.Light)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }

                saved.Add(preferences.Theme);
            },
            _ => { });

        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));
        queue.Enqueue(new UiPreferences(ThemePreference.FollowWindows));
        await firstStarted.Task;

        Assert.Empty(saved);

        releaseFirst.SetResult();
        await queue.DisposeAsync();

        Assert.Equal(
            [ThemePreference.Light, ThemePreference.Dark, ThemePreference.FollowWindows],
            saved);
    }

    [Fact]
    public async Task DisposeAsync_AwaitsTheFinalQueuedSave()
    {
        var saveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new UiPreferenceSaveQueue(
            async (_, _) =>
            {
                saveStarted.SetResult();
                await releaseSave.Task;
            },
            _ => { });
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));
        await saveStarted.Task;

        ValueTask disposal = queue.DisposeAsync();

        Assert.False(disposal.IsCompleted);
        releaseSave.SetResult();
        await disposal;
    }

    [Fact]
    public async Task SaveFailure_IsReportedAndDoesNotBlockLaterSelections()
    {
        var saved = new List<ThemePreference>();
        var reported = new List<Exception>();
        var queue = new UiPreferenceSaveQueue(
            (preferences, _) =>
            {
                if (preferences.Theme == ThemePreference.Light)
                {
                    throw new IOException("preference write failed");
                }

                saved.Add(preferences.Theme);
                return Task.CompletedTask;
            },
            reported.Add);

        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));
        await queue.DisposeAsync();

        Assert.Single(reported);
        Assert.Equal("preference write failed", reported[0].Message);
        Assert.Equal([ThemePreference.Dark], saved);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        int saves = 0;
        var queue = new UiPreferenceSaveQueue(
            (_, _) =>
            {
                saves++;
                return Task.CompletedTask;
            },
            _ => { });
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));

        await queue.DisposeAsync();
        await queue.DisposeAsync();

        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Enqueue_DoesNotRunFilePersistenceInlineOnTheUiCaller()
    {
        bool saverInvoked = false;
        var queue = new UiPreferenceSaveQueue(
            (_, _) =>
            {
                saverInvoked = true;
                return Task.CompletedTask;
            },
            _ => { });

        queue.Enqueue(new UiPreferences(ThemePreference.Dark));

        Assert.False(saverInvoked);
        await queue.DisposeAsync();
        Assert.True(saverInvoked);
    }
}
