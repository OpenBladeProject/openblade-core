using System.IO.Pipes;
using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Guardian.TestHost;

// Each mode runs in a separate test process. This pipe is a test fixture for
// disconnect delivery, with no production command or deployment surface.
internal static class Program
{
    private static readonly TimeSpan HostLimit = TimeSpan.FromMinutes(2);

    public static async Task<int> Main(string[] args)
    {
        switch (args)
        {
            case ["owner", var root, var pipeName, var gate] when gate is "fe-pending" or "zero-checkpoint":
                await RunOwner(root, pipeName, gate);
                return 0;
            case ["caller", var pipeName]:
                await RunCaller(pipeName);
                return 0;
            case ["probe-reuse", var root]:
                ProbeReuse(root);
                return 0;
            case ["launch-caller", var root, var mode, .. var payload]:
                await GuardianLaunchFixture.RunCaller(root, mode, payload);
                return 0;
            case ["launch-job-member", var root]:
                await GuardianLaunchFixture.RunJobMember(root);
                return 0;
            case ["launch-owner", var root, var mode, var control, var job, var caller, var unrelatedHandle, var eventName, .. var payload]:
                try { return await GuardianLaunchFixture.RunOwner(root, mode, control, job, caller, unrelatedHandle, eventName, payload); }
                catch (Exception error)
                {
                    WriteReport(root, "owner-error.json", error.ToString());
                    return 24;
                }
            case ["probe-shared-console"]:
                return GuardianLaunchFixture.ProbeSharedConsole();
            default:
                return 2;
        }
    }

    private static async Task RunOwner(string root, string pipeName, string gate)
    {
        using var timeout = new CancellationTokenSource(HostLimit);
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(timeout.Token);

        var fixture = new FakeTrial(root, gate);
        var guardian = So6120TrialGuardian.Start(fixture.Prepare, fixture.Clock);
        var monitor = MonitorCaller(pipe, guardian, root, timeout.Token);
        var receipt = await guardian.Completion.WaitAsync(timeout.Token);
        await timeout.CancelAsync();
        await monitor;
        WriteTerminalReport(root, receipt);
    }

    internal static void WriteTerminalReport(string root, So6120GuardianReceipt receipt)
    {
        WriteReport(root, "receipt.json", new TerminalReport(Environment.ProcessId, receipt.AutoConfirmed,
            receipt.CleanupConfirmed, receipt.JournalCompleted, receipt.ReleaseCompleted, receipt.Resolved,
            receipt.Failure, receipt.Trial?.StopReason.ToString(), receipt.Trial?.Entry?.Failure.ToString(),
            receipt.Trial?.Restoration?.Failure.ToString(), receipt.Trial?.StopStage?.ZeroPwmObservation is not null));
    }

    private static async Task MonitorCaller(NamedPipeServerStream pipe, So6120TrialGuardian guardian, string root,
        CancellationToken cancellationToken)
    {
        byte[] heartbeat = new byte[1];
        try
        {
            while (await pipe.ReadAsync(heartbeat, cancellationToken) != 0)
            {
                guardian.Heartbeat();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (IOException) { } // Windows can report caller termination as a broken pipe instead of EOF.

        guardian.CallerDisconnected();
        var snapshot = guardian.Snapshot;
        WriteReport(root, "disconnected.json", new DisconnectReport(Environment.ProcessId, snapshot.StopReason.ToString(),
            snapshot.OwnerRunning));
    }

    private static async Task RunCaller(string pipeName)
    {
        using var timeout = new CancellationTokenSource(HostLimit);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        byte[] heartbeat = [1];
        try
        {
            while (true)
            {
                await pipe.WriteAsync(heartbeat, timeout.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
        }
        catch (IOException) { } // The owner closed its end.
    }

    private static void ProbeReuse(string root)
    {
        var fixture = new FakeTrial(root);
        bool refused = false;
        try
        {
            using var session = fixture.CreateSession();
        }
        catch (IOException exception) when (exception.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7))
        {
            refused = true;
        }

        WriteReport(root, "reuse.json", new ReuseReport(Environment.ProcessId, refused, fixture.PortCalls));
    }

    internal static void WriteReport<T>(string root, string name, T report)
    {
        string path = Path.Combine(root, name);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report));
        File.Move(temporary, path);
    }
}

public sealed record PendingReport(int OwnerPid, int Commands, string Gate);

public sealed record DisconnectReport(int OwnerPid, string StopReason, bool OwnerRunning);

public sealed record TerminalReport(int OwnerPid, bool AutoConfirmed, bool CleanupConfirmed,
    bool JournalCompleted, bool ReleaseCompleted, bool Resolved, string? Failure, string? TrialStop,
    string? EntryFailure, string? RestorationFailure, bool ZeroPwmObserved);

public sealed record ReuseReport(int ProcessId, bool Refused, int PortCalls);
