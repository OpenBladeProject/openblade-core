using System.Diagnostics;

namespace OpenBlade.Service;

internal sealed class ServiceStartupTiming(long managedEntryQpc, Action<string> write) : IDisposable
{
    private readonly TaskCompletionSource reported =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int reportQueued;

    internal string HostTerminal { get; set; } = "host-failed";
    internal long NativeCheckStartQpc { get; set; }
    internal long NativeCheckEndQpc { get; set; }
    internal long HostBuildStartQpc { get; set; }
    internal long HostBuildEndQpc { get; set; }
    internal long HostRunStartQpc { get; set; }
    internal long WorkerInitializationStartQpc { get; set; }
    internal long WorkerInitializationEndQpc { get; set; }
    internal string WorkerInitializationOutcome { get; set; } = "not-started";
    internal long FirstListenerReadyQpc { get; set; }
    internal long FirstListenerFailureQpc { get; set; }
    internal long FirstPipeAcceptedQpc { get; set; }
    internal long FirstRequestReadStartQpc { get; set; }
    internal long FirstRequestReadEndQpc { get; set; }
    internal string FirstRequestReadOutcome { get; set; } = "not-started";

    internal Task Reported => reported.Task;

    public void Dispose()
    {
        // Flush the same one-shot report if the host exits before the first
        // request, or if its report is still pending.
        QueueReport(HostTerminal);
        Reported.Wait(TimeSpan.FromMilliseconds(100));
    }

    internal void QueueReport(string terminal)
    {
        if (Interlocked.Exchange(ref reportQueued, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                string host = FormattableString.Invariant(
                    $"ProcessId={Environment.ProcessId} QpcFrequency={Stopwatch.Frequency} ManagedEntryQpc={managedEntryQpc} NativeCheckStartQpc={NativeCheckStartQpc} NativeCheckEndQpc={NativeCheckEndQpc} HostBuildStartQpc={HostBuildStartQpc} HostBuildEndQpc={HostBuildEndQpc} HostRunStartQpc={HostRunStartQpc}");
                string worker = FormattableString.Invariant(
                    $"WorkerInitializationStartQpc={WorkerInitializationStartQpc} WorkerInitializationEndQpc={WorkerInitializationEndQpc} WorkerInitializationOutcome={WorkerInitializationOutcome}");
                string pipe = FormattableString.Invariant(
                    $"FirstListenerReadyQpc={FirstListenerReadyQpc} FirstListenerFailureQpc={FirstListenerFailureQpc} FirstPipeAcceptedQpc={FirstPipeAcceptedQpc} FirstRequestReadStartQpc={FirstRequestReadStartQpc} FirstRequestReadEndQpc={FirstRequestReadEndQpc} FirstRequestReadOutcome={FirstRequestReadOutcome}");
                write($"OpenBlade service startup timing. {host} {worker} {pipe} Terminal={terminal}");
            }
            catch (Exception)
            {
                // Timing cannot change service startup or pipe behavior.
            }
            finally
            {
                reported.TrySetResult();
            }
        });
    }
}
