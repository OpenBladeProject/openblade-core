using System.Diagnostics;

namespace OpenBlade.Tray;

public sealed record TrayExitSteps(
    Action UnregisterShutdown,
    Action DisposeShutdownEvent,
    Action DisposeTrayIcon,
    Func<ValueTask> DisposeViewModelAsync,
    Action CloseWindow,
    Func<ValueTask> DisposeInstanceCoordinatorAsync,
    Action ExitApplication);

public sealed class TrayExitLifecycle
{
    private readonly object sync = new();
    private readonly TrayExitSteps steps;
    private readonly Action<Exception> reportFailure;
    private Task? exitTask;
    private int exitRequested;

    public TrayExitLifecycle(TrayExitSteps steps, Action<Exception> reportFailure)
    {
        this.steps = steps ?? throw new ArgumentNullException(nameof(steps));
        this.reportFailure = reportFailure
            ?? throw new ArgumentNullException(nameof(reportFailure));
    }

    public bool ExitRequested => Volatile.Read(ref exitRequested) != 0;

    public Task ExitAsync()
    {
        lock (sync)
        {
            if (exitTask is not null)
            {
                return exitTask;
            }

            Volatile.Write(ref exitRequested, 1);
            exitTask = RunExitAsync();
            return exitTask;
        }
    }

    public bool TryRunWhileActive(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (sync)
        {
            if (ExitRequested)
            {
                return false;
            }

            action();
            return true;
        }
    }

    private async Task RunExitAsync()
    {
        RunStep(steps.UnregisterShutdown);
        RunStep(steps.DisposeShutdownEvent);
        RunStep(steps.DisposeTrayIcon);
        await RunStepAsync(steps.DisposeViewModelAsync);
        RunStep(steps.CloseWindow);
        await RunStepAsync(steps.DisposeInstanceCoordinatorAsync);
        RunStep(steps.ExitApplication);
    }

    private void RunStep(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private async ValueTask RunStepAsync(Func<ValueTask> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private void Report(Exception exception)
    {
        try
        {
            reportFailure(exception);
        }
        catch (Exception reportingException)
        {
            Trace.TraceError(
                "OpenBlade could not report a tray shutdown failure. Cleanup: {0}; reporting: {1}",
                exception,
                reportingException);
        }
    }
}
