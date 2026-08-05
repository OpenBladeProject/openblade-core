namespace OpenBlade.Tray;

public enum TrayExitChoice
{
    Cancel,
    KeepBackgroundRunning,
    ShutDownCompletely,
}

public sealed class TrayExitController
{
    private readonly Func<TrayExitChoice> requestChoice;
    private readonly Func<Task> keepBackgroundRunning;
    private readonly Func<Task> shutDownCompletely;
    private int requestActive;

    public TrayExitController(
        Func<TrayExitChoice> requestChoice,
        Func<Task> keepBackgroundRunning,
        Func<Task> shutDownCompletely)
    {
        this.requestChoice = requestChoice
            ?? throw new ArgumentNullException(nameof(requestChoice));
        this.keepBackgroundRunning = keepBackgroundRunning
            ?? throw new ArgumentNullException(nameof(keepBackgroundRunning));
        this.shutDownCompletely = shutDownCompletely
            ?? throw new ArgumentNullException(nameof(shutDownCompletely));
    }

    public async Task RequestExitAsync()
    {
        if (Interlocked.Exchange(ref requestActive, 1) != 0)
        {
            return;
        }

        try
        {
            switch (requestChoice())
            {
                case TrayExitChoice.KeepBackgroundRunning:
                    await keepBackgroundRunning();
                    break;
                case TrayExitChoice.ShutDownCompletely:
                    await shutDownCompletely();
                    break;
                case TrayExitChoice.Cancel:
                    break;
                default:
                    throw new InvalidOperationException("The tray exit choice is invalid.");
            }
        }
        finally
        {
            Volatile.Write(ref requestActive, 0);
        }
    }
}
