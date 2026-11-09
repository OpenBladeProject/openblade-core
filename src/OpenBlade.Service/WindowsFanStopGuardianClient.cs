using System.Diagnostics;
using System.IO.Pipes;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

// The service never opens InpOut. Its native exclusion lease is held by the
// backend until this client has observed the original owner exit AND its full
// terminal receipt. Pipe EOF, a matching fan value or process exit is insufficient.
internal sealed class WindowsFanStopGuardianClient : IFanStopGuardianClient, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly FanStopGuardianStore store;
    private readonly Func<CancellationToken, Task<FanStopAdmission>> admission;
    private readonly Func<FanStopAdmission> continuationSnapshot;
    private readonly string executable;
    private readonly FanStopFirmwareAvailability firmwareAvailability;
    private readonly bool guardianAvailable;
    private FanStopGuardianSnapshot snapshot = new(Guid.Empty, 0, FanStopGuardianPhase.Idle);
    private Task? running;
    private bool autoRequested;
    private bool disposed;

    internal WindowsFanStopGuardianClient(string machineDataDirectory,
        Func<CancellationToken, Task<FanStopAdmission>> admission,
        Func<FanStopAdmission> continuationSnapshot,
        FanStopFirmwareAvailability? firmwareAvailability = null)
    {
        this.admission = admission;
        this.continuationSnapshot = continuationSnapshot;
        this.firmwareAvailability = firmwareAvailability ?? FanStopFirmwareAvailability.CreateWindows();
        store = new(machineDataDirectory);
        executable = Path.Combine(AppContext.BaseDirectory, "OpenBlade.FanStop.Guardian.exe");
        StartupDisposition = store.InspectStartup();
        try
        {
            FanStopGuardianTransport.RequireSystem();
            FanStopGuardianTransport.VerifyProtectedPath(executable, directory: false);
            FanStopGuardianTransport.VerifyProtectedPath(AppContext.BaseDirectory, directory: true);
            guardianAvailable = true;
        }
        catch { guardianAvailable = false; }
    }

    public FanStopUnavailableReason UnavailableReason => guardianAvailable
        ? firmwareAvailability.Read().Reason : FanStopUnavailableReason.GuardianUnavailable;
    public string? UnavailableMessage => guardianAvailable ? firmwareAvailability.Read().Message : null;
    public FanStopStartupDisposition StartupDisposition { get; }
    public FanStopGuardianSnapshot Snapshot { get { lock (sync) { return snapshot; } } }
    public event Action? SnapshotChanged;

    public Task<FanStopAdmission> ReadAdmissionAsync(CancellationToken token) => admission(token);

    public Task<FanStopSubmission> StartAsync(Guid operationId, CancellationToken token) =>
        StartAsync(operationId, FanStopSettings.Default, token);

    public Task<FanStopSubmission> StartAsync(Guid operationId, FanStopSettings settings, CancellationToken token)
    {
        if (!FanStopSettings.IsWellFormed(settings)) { throw new ArgumentException("Invalid Fan Stop settings.", nameof(settings)); }
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (disposed || operationId == Guid.Empty || !guardianAvailable
                || StartupDisposition != FanStopStartupDisposition.Clear || running is { IsCompleted: false }
                || snapshot.OperationId != Guid.Empty && !snapshot.Released)
            { return Task.FromResult(FanStopSubmission.NotSubmitted); }
            if (firmwareAvailability.Read(refresh: true).Reason != FanStopUnavailableReason.None)
            { return Task.FromResult(FanStopSubmission.NotSubmitted); }
            using var self = Process.GetCurrentProcess();
            bool reserved = false;
            try
            {
                store.Reserve(new(operationId, self.Id, self.StartTime.ToUniversalTime().Ticks) { Settings = settings });
                reserved = true;
                // The marker exists before any child can start. Startup failure
                // after launch retains it, even if the child did not reach hardware.
                string pipeName = $"OpenBlade.FanStop.{Environment.ProcessId}.{Guid.NewGuid():N}";
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
                    FanStopGuardianTransport.MaximumFrameBytes, FanStopGuardianTransport.MaximumFrameBytes);
                Process? proxy = null;
                try
                {
                    var start = new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = AppContext.BaseDirectory,
                    };
                    foreach (string arg in new[] { "--proxy", pipeName, operationId.ToString("N"), store.DirectoryPath })
                    { start.ArgumentList.Add(arg); }
                    proxy = Process.Start(start) ?? throw new IOException("Guardian proxy did not start.");
                    autoRequested = false;
                    snapshot = new(operationId, 1, FanStopGuardianPhase.Starting, CommandPending: true);
                    running = Task.Run(() => RunAsync(operationId, pipe, proxy));
                    return Task.FromResult(FanStopSubmission.Submitted);
                }
                catch
                {
                    pipe.Dispose();
                    if (proxy is not null)
                    {
                        // Never terminate a proxy or owner to make retry possible.
                        snapshot = new(operationId, 1, FanStopGuardianPhase.RecoveryRequired, Unresolved: true,
                            Failure: "Guardian startup became uncertain.");
                        proxy.Dispose();
                        return Task.FromResult(FanStopSubmission.Uncertain);
                    }
                    throw;
                }
            }
            catch
            {
                if (reserved)
                {
                    try { store.AbandonBeforeLaunch(operationId); }
                    catch
                    {
                        snapshot = new(operationId, 1, FanStopGuardianPhase.RecoveryRequired, Unresolved: true,
                            Failure: "Guardian startup reservation could not be resolved.");
                        return Task.FromResult(FanStopSubmission.Uncertain);
                    }
                }
                return Task.FromResult(FanStopSubmission.NotSubmitted);
            }
        }
    }

    public Task<FanStopSubmission> RequestAutoAsync(Guid operationId, CancellationToken token)
    {
        // Once this operation exists, caller cancellation may not suppress its
        // already-authorized handback. Repeated Auto is a latched signal only.
        lock (sync)
        {
            if (operationId != snapshot.OperationId || operationId == Guid.Empty)
            { return Task.FromResult(FanStopSubmission.NotSubmitted); }
            autoRequested = true;
            return Task.FromResult(snapshot.Unresolved ? FanStopSubmission.Uncertain : FanStopSubmission.Submitted);
        }
    }

    private async Task RunAsync(Guid operation, NamedPipeServerStream pipe, Process proxy)
    {
        using (pipe)
        using (proxy)
        using (var stopPump = new CancellationTokenSource())
        {
            Task pump = Task.CompletedTask;
            Process? owner = null;
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var identityTask = FanStopGuardianTransport.ReadAsync<FanStopGuardianOwnerIdentity>(
                    proxy.StandardOutput.BaseStream, startup.Token);
                await pipe.WaitForConnectionAsync(startup.Token);
                var identity = await identityTask;
                if (FanStopGuardianTransport.PeerPid(pipe, server: true) != identity.Pid)
                { throw new UnauthorizedAccessException("Guardian pipe is not connected to the launched owner."); }
                owner = FanStopGuardianTransport.RetainPeer(identity.Pid, executable, identity.StartUtcTicks);
                pump = PumpAsync(operation, pipe, stopPump.Token);
                long ownerRevision = 0;
                while (true)
                {
                    var next = await FanStopGuardianTransport.ReadAsync<FanStopGuardianSnapshot>(pipe, CancellationToken.None);
                    if (next.OperationId != operation || next.Revision <= ownerRevision || next.OwnerExited
                        || !Enum.IsDefined(next.Phase) || !Enum.IsDefined(next.ConfirmedState) || !Enum.IsDefined(next.EndReason)
                        || next.Reason?.Length > 1024 || next.Failure?.Length > 1024)
                    { throw new InvalidDataException("Guardian status generation or shape is invalid."); }
                    ownerRevision = next.Revision;
                    if (next.Phase is FanStopGuardianPhase.Completed or FanStopGuardianPhase.Refused)
                    {
                        // The owner has released resources, but only the service
                        // can attest its retained process object's actual exit.
                        await owner.WaitForExitAsync();
                        if (store.ReadOwnerResult(operation) != next)
                        { throw new InvalidDataException("Durable guardian completion differs from its live receipt."); }
                        next = next with { OwnerExited = true };
                        if (owner.ExitCode != 0 || !next.Released)
                        { throw new InvalidDataException("Guardian exit did not confirm complete Auto handback."); }
                        store.Finish(operation);
                        Publish(next);
                        return;
                    }
                    Publish(next);
                    if (next.Unresolved || next.Phase == FanStopGuardianPhase.RecoveryRequired) { return; }
                }
            }
            catch (Exception error)
            {
                var prior = Snapshot;
                Publish(prior with
                {
                    Phase = FanStopGuardianPhase.RecoveryRequired,
                    Unresolved = true,
                    Failure = $"Guardian channel failed ({error.GetType().Name}).",
                    CommandPending = false,
                });
            }
            finally
            {
                await stopPump.CancelAsync();
                // Closing this pipe requests owner Auto. It does not prove it.
                pipe.Dispose();
                try { await pump; } catch (Exception) { }
                owner?.Dispose();
            }
        }
    }

    internal async Task PumpAsync(Guid operation, Stream pipe, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool automatic;
                lock (sync) { automatic = autoRequested || disposed; }
                FanStopAdmission? fresh = null;
                if (!automatic)
                {
                    // Heartbeats must never invoke the gated entry reader. In
                    // particular, release can race this loop and would turn an
                    // entry callback into ordinary HID outside operationGate.
                    try { fresh = continuationSnapshot(); }
                    catch (Exception) { automatic = true; }
                    automatic |= fresh is null || !fresh.CanStop;
                }
                using var write = CancellationTokenSource.CreateLinkedTokenSource(token);
                write.CancelAfter(TimeSpan.FromSeconds(2));
                await FanStopGuardianTransport.WriteAsync(pipe, new FanStopGuardianCommand(operation,
                    automatic ? FanStopGuardianCommandKind.Automatic : FanStopGuardianCommandKind.Heartbeat,
                    fresh?.ObservedPowerSource, !automatic), write.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            }
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // Heartbeat loss makes the independent owner request Auto. Never
            // reconnect or replay a command after a partially written frame.
            pipe.Dispose();
        }
    }

    private void Publish(FanStopGuardianSnapshot value)
    {
        lock (sync) { snapshot = value with { Revision = checked(snapshot.Revision + 1) }; }
        try { SnapshotChanged?.Invoke(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task? active;
        lock (sync) { disposed = true; autoRequested = true; active = running; }
        if (active is not null)
        {
            try { await active.WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (TimeoutException) { /* The surviving owner keeps its resources. */ }
        }
    }
}
