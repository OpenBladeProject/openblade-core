namespace OpenBlade.Tray;

public sealed class UiPreferenceSaveQueue : IAsyncDisposable
{
    private readonly Func<UiPreferences, CancellationToken, Task> saveAsync;
    private readonly Action<Exception> reportFailure;
    private readonly object sync = new();
    private Task tail = Task.CompletedTask;
    private bool accepting = true;

    public UiPreferenceSaveQueue(
        Func<UiPreferences, CancellationToken, Task> saveAsync,
        Action<Exception> reportFailure)
    {
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        this.reportFailure = reportFailure
            ?? throw new ArgumentNullException(nameof(reportFailure));
    }

    public void Enqueue(UiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(!accepting, this);
            Task predecessor = tail;
            tail = Task.Run(() => SaveAfterAsync(predecessor, preferences));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task completion;
        lock (sync)
        {
            accepting = false;
            completion = tail;
        }

        await completion.ConfigureAwait(false);
    }

    private async Task SaveAfterAsync(Task predecessor, UiPreferences preferences)
    {
        await predecessor.ConfigureAwait(false);

        try
        {
            await saveAsync(preferences, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                reportFailure(exception);
            }
            catch
            {
                // A diagnostic sink must not break preference persistence ordering.
            }
        }
    }
}
