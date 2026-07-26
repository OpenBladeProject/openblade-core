namespace OpenBlade.Tray.Tests;

public sealed class UiPreferenceSaveQueueTests
{
    [Fact]
    public async Task Enqueue_CoalescesSnapshotsSupersededWhileASaveIsInFlight()
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
        await firstStarted.Task;
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));
        queue.Enqueue(new UiPreferences(ThemePreference.FollowWindows));

        releaseFirst.SetResult();
        await queue.DisposeAsync();

        Assert.Equal(
            [ThemePreference.Light, ThemePreference.FollowWindows],
            saved);
    }

    [Fact]
    public async Task Enqueue_PersistsEverySnapshotThatIsNotSuperseded()
    {
        var firstSaved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<ThemePreference>();
        var queue = new UiPreferenceSaveQueue(
            (preferences, _) =>
            {
                saved.Add(preferences.Theme);
                if (preferences.Theme == ThemePreference.Light)
                {
                    firstSaved.SetResult();
                }

                return Task.CompletedTask;
            },
            _ => { });

        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        await firstSaved.Task;
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));
        await queue.DisposeAsync();

        Assert.Equal([ThemePreference.Light, ThemePreference.Dark], saved);
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
    public async Task DisposeAsync_FlushesTheSnapshotScheduledDuringAnInFlightSave()
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
        await firstStarted.Task;
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));
        ValueTask disposal = queue.DisposeAsync();

        Assert.False(disposal.IsCompleted);
        releaseFirst.SetResult();
        await disposal;

        Assert.Equal([ThemePreference.Light, ThemePreference.Dark], saved);
    }

    [Fact]
    public async Task SaveFailure_IsReportedAndTheNextSnapshotStillPersists()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSaved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<ThemePreference>();
        var reported = new List<Exception>();
        var queue = new UiPreferenceSaveQueue(
            async (preferences, _) =>
            {
                if (preferences.Theme == ThemePreference.Light)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                    throw new UiPreferenceSaveException("preference write failed");
                }

                saved.Add(preferences.Theme);
                secondSaved.SetResult();
            },
            reported.Add);

        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        await firstStarted.Task;
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));
        releaseFirst.SetResult();
        await secondSaved.Task;
        await queue.DisposeAsync();

        UiPreferenceSaveException failure =
            Assert.IsType<UiPreferenceSaveException>(Assert.Single(reported));
        Assert.Equal("preference write failed", failure.Message);
        Assert.Equal([ThemePreference.Dark], saved);
    }

    [Fact]
    public async Task SaveFailure_DuringDisposalDrainStillFlushesThePendingSnapshot()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<ThemePreference>();
        var reported = new List<Exception>();
        var queue = new UiPreferenceSaveQueue(
            async (preferences, _) =>
            {
                if (preferences.Theme == ThemePreference.Light)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                    throw new UiPreferenceSaveException("preference write failed");
                }

                saved.Add(preferences.Theme);
            },
            reported.Add);

        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        await firstStarted.Task;
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));
        ValueTask disposal = queue.DisposeAsync();

        releaseFirst.SetResult();
        await disposal;

        Assert.Single(reported);
        Assert.Equal([ThemePreference.Dark], saved);
    }

    [Fact]
    public async Task ReportFailureThrowing_DoesNotWedgeTheQueue()
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
                    throw new UiPreferenceSaveException("preference write failed");
                }

                saved.Add(preferences.Theme);
            },
            _ => throw new InvalidOperationException("diagnostic sink failed"));

        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        await firstStarted.Task;
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));
        releaseFirst.SetResult();
        await queue.DisposeAsync();

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
    public async Task DisposeAsync_CalledConcurrentlyCompletesEveryCaller()
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

        Task first = queue.DisposeAsync().AsTask();
        Task second = queue.DisposeAsync().AsTask();

        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        releaseSave.SetResult();
        await Task.WhenAll(first, second);
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

    [Fact]
    public async Task Enqueue_DoesNotRunPersistenceInlineWhileASaveIsInFlight()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondInvoked = false;
        var queue = new UiPreferenceSaveQueue(
            async (preferences, _) =>
            {
                if (preferences.Theme == ThemePreference.Light)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
                else
                {
                    secondInvoked = true;
                }
            },
            _ => { });

        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        await firstStarted.Task;
        queue.Enqueue(new UiPreferences(ThemePreference.Dark));

        Assert.False(secondInvoked);
        releaseFirst.SetResult();
        await queue.DisposeAsync();
        Assert.True(secondInvoked);
    }

    [Fact]
    public async Task Enqueue_ThrowsObjectDisposedExceptionAfterDisposal()
    {
        var queue = new UiPreferenceSaveQueue(
            (_, _) => Task.CompletedTask,
            _ => { });

        await queue.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(
            () => queue.Enqueue(new UiPreferences(ThemePreference.Dark)));
    }

    [Fact]
    public async Task Enqueue_ThrowsObjectDisposedExceptionOnceDisposalStarts()
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
        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        await saveStarted.Task;

        ValueTask disposal = queue.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(
            () => queue.Enqueue(new UiPreferences(ThemePreference.Dark)));
        releaseSave.SetResult();
        await disposal;
    }

    [Fact]
    public async Task WorkerFault_RejectsLaterEnqueueWithInnerCause()
    {
        var reported = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new UiPreferenceSaveQueue(
            (_, _) => throw new TerminalTestException("worker failed"),
            reported.SetResult);
        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        Exception failure = await reported.Task;

        InvalidOperationException rejection = Assert.Throws<InvalidOperationException>(
            () => queue.Enqueue(new UiPreferences(ThemePreference.Dark)));

        Assert.Same(failure, rejection.InnerException);
        await Assert.ThrowsAsync<TerminalTestException>(
            () => queue.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task WorkerFault_IsRethrownByDisposeAsyncAfterCleanup()
    {
        var reported = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new UiPreferenceSaveQueue(
            (_, _) => throw new TerminalTestException("worker failed"),
            reported.SetResult);
        queue.Enqueue(new UiPreferences(ThemePreference.Light));
        Exception failure = await reported.Task;

        TerminalTestException disposalFailure =
            await Assert.ThrowsAsync<TerminalTestException>(
                () => queue.DisposeAsync().AsTask());

        Assert.Same(failure, disposalFailure);
        await Assert.ThrowsAsync<TerminalTestException>(
            () => queue.DisposeAsync().AsTask());
    }

    [Fact]
    public void Constructor_RejectsNullDelegates()
    {
        Assert.Throws<ArgumentNullException>(
            () => new UiPreferenceSaveQueue(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(
            () => new UiPreferenceSaveQueue((_, _) => Task.CompletedTask, null!));
    }

    [Fact]
    public async Task Enqueue_RejectsNullPreferences()
    {
        var queue = new UiPreferenceSaveQueue(
            (_, _) => Task.CompletedTask,
            _ => { });

        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!));

        await queue.DisposeAsync();
    }

    private sealed class TerminalTestException(string message) : Exception(message);
}
