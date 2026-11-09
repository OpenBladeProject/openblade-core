using System.Diagnostics;

namespace OpenBlade.Service;

internal sealed class ServiceStartupTiming(long managedEntryQpc, Action<string> write) : IDisposable
{
    private string? queuedTerminal;
    private int summaryClaimed;

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

    public void Dispose()
    {
        // After host teardown, publish any summary the background writer has
        // yet to claim. There is no timed wait or dependency on its completion.
        ReportOnce(Volatile.Read(ref queuedTerminal) ?? HostTerminal);
    }

    internal void QueueReport(string terminal)
    {
        Volatile.Write(ref queuedTerminal, terminal);
        _ = Task.Run(() => ReportOnce(terminal));
    }

    internal void RecordFirstListenerFailure()
    {
        // The first-listener gate already serializes creation attempts. Emit
        // the first failure even if there is never a connection or clean exit.
        if (FirstListenerFailureQpc != 0)
        {
            return;
        }

        long failureQpc = FirstListenerFailureQpc = Stopwatch.GetTimestamp();
        _ = Task.Run(() =>
        {
            try
            {
                write(FormattableString.Invariant(
                    $"OpenBlade service startup timing. ProcessId={Environment.ProcessId} QpcFrequency={Stopwatch.Frequency} ManagedEntryQpc={managedEntryQpc} WorkerInitializationEndQpc={WorkerInitializationEndQpc} FirstListenerFailureQpc={failureQpc} Terminal=first-listener-failed"));
            }
            catch (Exception)
            {
                // Timing cannot change service startup or pipe behavior.
            }
        });
    }

    private void ReportOnce(string terminal)
    {
        if (Interlocked.Exchange(ref summaryClaimed, 1) != 0)
        {
            return;
        }

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
    }
}
