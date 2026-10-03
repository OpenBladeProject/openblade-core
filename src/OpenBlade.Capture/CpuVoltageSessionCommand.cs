using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed class CpuVoltageSessionCommand
{
    internal const string HostName = "cpu-voltage-session-host", ClientName = "cpu-voltage-session-client";
    internal const string Confirmation = "RZ09-0528-CPU-SESSION-ONE-OBSERVATION-ONE-CYCLE";
    private static CpuVoltageSessionCommand? retainedHost;
    private readonly object gate = new();
    private readonly WindowsCpuVoltageSessionPlatform platform;
    private readonly CpuVoltageSessionState state;
    private readonly ICpuPmContinuityPlatform continuity = WindowsCpuPmSessionContinuity.CreateParentPassivePlatform();
    private readonly long started = Stopwatch.GetTimestamp();
    private CpuPmContinuityReading initial;
    private Process? owner;
    private CpuPmRetainedLaunchProcess? retainedOwner;
    private Task? job;
    private bool disposed;
    private CpuVoltageSessionCommand(CpuVoltageOwnedSmokeCommand.Options options)
    {
        platform = new(options); state = new(platform.Enrollment.Session); initial = continuity.Read();
        if (!ContextCurrent()) { platform.Dispose(); throw new IOException("An unlocked active console on AC is required."); }
    }
    internal static CpuVoltageOwnedSmokeCommand.Options ParseHost(string[] args)
    {
        if (args.Length != 10) { throw new ArgumentException("Five fixed enrollment options are required."); }
        int confirmation = Array.IndexOf(args, "--confirm-target");
        if (confirmation < 0 || confirmation + 1 >= args.Length || args[confirmation + 1] != Confirmation)
        { throw new ArgumentException("Exact session-host confirmation is required."); }
        string[] ownerArgs = args.ToArray(); ownerArgs[confirmation + 1] = CpuVoltageOwnedSmokeCommand.Confirmation;
        return CpuVoltageOwnedSmokeCommand.Parse(ownerArgs);
    }
    internal static async Task<int> RunHostAsync(string[] args, TextWriter output)
    {
        if (args is ["--help"])
        {
            output.WriteLine($"{HostName} --payload <reviewed private closure> --source <40hex> --manifest-sha256 <64HEX> --expected-service-version <exact> --confirm-target {Confirmation}");
            output.WriteLine("Start elevated once, attended. No automatic elevation. Eight-hour enrollment, at most one observation and one reversible cycle. Remain on AC with the same console unlocked.");
            return 0;
        }
        try
        {
            var options = ParseHost(args);
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || retainedHost is not null) { return 2; }
            retainedHost = new(options);
            output.WriteLine("CpuVoltageSession=" + retainedHost.platform.Enrollment.Session.ToString("N")); output.Flush();
            return await retainedHost.ServeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { output.WriteLine("Session host refused: " + exception.Message); return 3; }
    }
    private static string PipeName(Guid session) => "OpenBlade.CpuVoltageSession." + session.ToString("N");
    private async Task<int> ServeAsync()
    {
        using var monitoring = new CancellationTokenSource();
        Task monitor = MonitorAsync(monitoring.Token);
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            lock (gate) { state.Block("Host interrupted; no further jobs. Retained owners are not terminated."); }
        };
        Console.CancelKeyPress += cancel;
        try
        {
            while (true)
            {
                lock (gate)
                {
                    if (state.Snapshot.State == "Closed" || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromHours(8)
                        && state.Snapshot.Job is null) { break; }
                }
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                security.AddAccessRule(new(new SecurityIdentifier(platform.Enrollment.UserSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName(platform.Enrollment.Session), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.FirstPipeInstance,
                    4096, 4096, security);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                    Authorize(pipe);
                    var request = CpuVoltageSessionProtocol.Parse(await CpuVoltageSessionProtocol.ReadAsync(pipe,
                        CpuVoltageSessionProtocol.MaximumRequest, timeout.Token).ConfigureAwait(false));
                    CpuVoltageSessionReply response;
                    lock (gate)
                    {
                        if (request.Action is CpuVoltageSessionAction.ObserveOnly or CpuVoltageSessionAction.VoltageCycle && !ContextCurrent())
                        { state.Block("Session/power continuity changed. No new job permitted."); }
                        bool launch = state.Accept(request, value => platform.WriteNew("accepted-" + value.RequestId.ToString("N") + ".json", value));
                        if (launch) { job = Task.Run(() => RunJobAsync(request)); }
                        response = state.Snapshot with { RequestAccepted = launch };
                    }
                    await CpuVoltageSessionProtocol.WriteAsync(pipe, response, CpuVoltageSessionProtocol.MaximumReply, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { /* A rejected/disconnected client never cancels or repeats a job. */ }
            }
        }
        catch (Exception exception)
        {
            bool retain;
            lock (gate) { state.Block("Session server unavailable: " + exception.Message); retain = state.MustRetainHost; }
            // Fatal pipe/authentication/server failures cannot retire the root of
            // an accepted job. No replacement server, child or cleanup is started.
            if (retain) { await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false); }
            platform.Dispose(); disposed = true;
            throw;
        }
        finally
        {
            monitoring.Cancel(); await monitor.ConfigureAwait(false); Console.CancelKeyPress -= cancel;
        }
        platform.Dispose(); disposed = true; return 0;
    }
    private void Authorize(NamedPipeServerStream pipe)
    {
        bool authorized = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true);
            authorized = identity?.User?.Value == platform.Enrollment.UserSid
                && identity.Groups?.Any(group => group.Value == "S-1-5-4") == true;
        });
        if (!authorized || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid > int.MaxValue)
        { throw new UnauthorizedAccessException("Enrolled interactive user required."); }
        using var process = Process.GetProcessById((int)pid);
        if (process.SessionId != platform.Enrollment.WindowsSession
            || WindowsCpuVoltageSessionPlatform.WTSGetActiveConsoleSessionId() != process.SessionId)
        { throw new UnauthorizedAccessException("Enrolled active console session required."); }
    }
    private bool ContextCurrent()
    {
        var current = continuity.Read();
        if (disposed || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromHours(8)
            || !initial.Available || !current.Available || !current.OnAc || !current.LocalActiveDefault
            || current.SessionId != initial.SessionId || current.SessionId != platform.Enrollment.WindowsSession
            || current.TickMilliseconds < initial.TickMilliseconds || current.Unbiased100Nanoseconds < initial.Unbiased100Nanoseconds) { return false; }
        ulong ticks = (current.TickMilliseconds - initial.TickMilliseconds) * 10_000;
        ulong awake = current.Unbiased100Nanoseconds - initial.Unbiased100Nanoseconds;
        return (ticks >= awake ? ticks - awake : awake - ticks) < 2_500_000;
    }
    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                lock (gate) { if (!ContextCurrent()) { state.Block("Session, AC, awake continuity or enrollment deadline changed. No new jobs."); } }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { lock (gate) { state.Block("Session monitoring unavailable. No new jobs."); } }
    }
    private async Task RunJobAsync(CpuVoltageSessionRequest request)
    {
        Guid run = Guid.Empty;
        DateTimeOffset launched = DateTimeOffset.UtcNow;
        Dictionary<string, JsonElement>? records = null;
        try
        {
            using var budget = new CancellationTokenSource(CpuVoltageOwnedSmokeCommand.Budget);
            lock (gate) { if (state.Snapshot.State != "Running") { throw new IOException("Job admission closed before preparation."); } }
            if (!ContextCurrent()) { throw new IOException("Session admission changed before launch."); }
            platform.PrepareService(budget.Token);
            lock (gate) { if (state.Snapshot.State != "Running") { throw new IOException("Job admission closed before owner launch."); } }
            if (!ContextCurrent()) { throw new IOException("Session admission changed after Service preparation."); }
            var image = platform.OwnerImage();
            owner = Process.Start(platform.OwnerStart(request.Action)) ?? throw new IOException("Owner launch outcome unavailable.");
            retainedOwner = CpuPmRetainedLaunchProcess.RetainForCompletion(WindowsCpuPmProcessProbe.FromLaunchedHandle(owner.SafeHandle),
                platform.Pins, platform.Image, image.Sha256, image);
            // Fixed short announcement comes from the retained actual owner, never a caller path.
            Task read = ReadOwnerOutputAsync(owner, value => run = value);
            await owner.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            await read.WaitAsync(budget.Token).ConfigureAwait(false);
            if (retainedOwner.ReadState() != CpuPmChildState.Terminal || owner.ExitCode != 0 || run == Guid.Empty)
            { throw new IOException("Owner did not produce verified terminal success."); }
            records = platform.ReadEvidence(run);
            bool admitted = VerifyCompletion(platform.Enrollment, request.Action, run, records, launched, DateTimeOffset.UtcNow);
            if (!new WindowsCpuPmDriverLifecyclePlatform().ReadIndependentAbsence().IsAbsent)
            { throw new IOException("Owned-driver absence no longer confirmed."); }
            platform.ReleaseVerifiedTerminalService();
            retainedOwner.ReleaseAfterTerminal(); retainedOwner = null; owner.Dispose(); owner = null;
            platform.WriteNew("result-" + request.RequestId.ToString("N") + ".json", records);
            lock (gate) { state.Complete(true, admitted, records, admitted ? "Verified completion and owned cleanup." : "Observation recorded and cleaned; admission refused. Cycle blocked."); }
        }
        catch (Exception exception)
        {
            try { if (run != Guid.Empty) { records = platform.ReadEvidence(run); } } catch { }
            lock (gate) { state.Complete(false, false, records, "Stopped without retry: " + exception.Message + " Retained owner/resources require explicit reconciliation."); }
        }
    }
    internal static bool VerifyCompletion(CpuVoltageSessionEnrollment enrollment, CpuVoltageSessionAction action, Guid run,
        IReadOnlyDictionary<string, JsonElement> records, DateTimeOffset launched, DateTimeOffset completed)
    {
        var receipt = records["control/voltage-owner-completed.json"];
        string mode = action == CpuVoltageSessionAction.ObserveOnly ? "ObserveOnly" : "VoltageCycle";
        if (receipt.GetProperty("Run").GetGuid() != run || receipt.GetProperty("Mode").GetString() != mode
            || receipt.GetProperty("Source").GetString() != enrollment.Source || receipt.GetProperty("ManifestSha256").GetString() != enrollment.Manifest
            || receipt.GetProperty("ExpectedServiceVersion").GetString() != enrollment.ServiceVersion
            || !receipt.GetProperty("ServiceTerminal").GetBoolean() || !receipt.GetProperty("OwnedDriverAbsent").GetBoolean()
            || receipt.GetProperty("OriginalVerified").GetBoolean() != (action == CpuVoltageSessionAction.VoltageCycle))
        { throw new InvalidDataException("Completion receipt does not match the enrolled job."); }
        var result = records["evidence/cpu-voltage-optimizer.json"];
        if (result.GetProperty("expectedServiceVersion").GetString() != enrollment.ServiceVersion)
        { throw new InvalidDataException("Evidence Service version differs."); }
        if (action == CpuVoltageSessionAction.VoltageCycle)
        {
            CpuVoltageOwnedSmokeEvidence.Verify(System.Text.Encoding.UTF8.GetBytes(records["evidence/cpu-voltage-optimizer-baseline.json"].GetRawText()),
                System.Text.Encoding.UTF8.GetBytes(result.GetRawText()), enrollment.ServiceVersion, launched, completed);
            return true;
        }
        CpuVoltageOwnedSmokeEvidence.VerifyObservation(System.Text.Encoding.UTF8.GetBytes(result.GetRawText()), enrollment.ServiceVersion, launched, completed);
        var response = result.GetProperty("controlResponse").Deserialize<ControlOperationResult>(CpuVoltageSessionProtocol.Json);
        return response is { Status: ControlOperationStatus.Stored, CpuTuning: { IsWellFormed: true, Live.CanWrite: true } };
    }
    private static async Task ReadOwnerOutputAsync(Process process, Action<Guid> announce)
    {
        int total = 0; bool announced = false;
        char[] buffer = new char[256]; var pending = new System.Text.StringBuilder();
        int count;
        while ((count = await process.StandardOutput.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if ((total += count) > 16384) { throw new IOException("Owner output exceeded its bound."); }
            foreach (char character in buffer.AsSpan(0, count).ToArray())
            {
                if (character != '\n') { pending.Append(character); continue; }
                string line = pending.ToString().TrimEnd('\r'); pending.Clear();
                const string prefix = "CpuVoltageOwnerRun=";
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
                string text = line[prefix.Length..];
                if (announced || !Guid.TryParseExact(text, "N", out Guid run) || run == Guid.Empty || text != run.ToString("N"))
                { throw new IOException("Invalid owner run announcement."); }
                announced = true; announce(run);
            }
        }
    }
    internal static async Task<int> RunClientAsync(string[] args, TextWriter output)
    {
        if (args is ["--help"])
        { output.WriteLine($"{ClientName} --session <N-guid> --action ObserveOnly|VoltageCycle|Status|Close [--request-id <N-guid>]. No elevation, service start, polling or retry."); return 0; }
        try
        {
            if (args.Length is not (4 or 6) || args[0] != "--session" || !Guid.TryParseExact(args[1], "N", out Guid session)
                || args[2] != "--action" || !Enum.TryParse(args[3], out CpuVoltageSessionAction action) || action.ToString() != args[3]
                || !Enum.IsDefined(action)) { throw new ArgumentException("Invalid fixed session client options."); }
            Guid requestId = Guid.NewGuid();
            if (args.Length == 6 && (args[4] != "--request-id" || !Guid.TryParseExact(args[5], "N", out requestId) || requestId == Guid.Empty))
            { throw new ArgumentException("Invalid request ID."); }
            var enrollment = WindowsCpuVoltageSessionPlatform.ReadEnrollment(session);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var pipe = new NamedPipeClientStream(".", PipeName(session), PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != enrollment.HostPid)
            { throw new IOException("Pipe server is not the enrolled host."); }
            // Query/synchronize only; Process.SafeHandle may request broad access
            // that a normal-integrity client cannot obtain to an elevated host.
            using var handle = OpenProcess(0x00101000, false, enrollment.HostPid);
            if (handle.IsInvalid) { throw new IOException("Enrolled host process cannot be inspected."); }
            using var probe = WindowsCpuPmProcessProbe.FromLaunchedHandle(handle);
            var peer = probe.Read(true);
            if (peer.Created != enrollment.HostCreated || peer.State != CpuPmChildState.Running || peer.Image != enrollment.HostImage)
            { throw new IOException("Enrolled host process changed."); }
            output.WriteLine("RequestId=" + requestId.ToString("N")); output.Flush();
            await CpuVoltageSessionProtocol.WriteAsync(pipe, new CpuVoltageSessionRequest(1, requestId, action),
                CpuVoltageSessionProtocol.MaximumRequest, deadline.Token).ConfigureAwait(false);
            byte[] bytes = await CpuVoltageSessionProtocol.ReadAsync(pipe, CpuVoltageSessionProtocol.MaximumReply, deadline.Token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CpuVoltageSessionReply>(bytes, CpuVoltageSessionProtocol.Json)
                ?? throw new IOException("Missing response.");
            if (response.Session != session) { throw new IOException("Session response differs."); }
            output.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
            return action is CpuVoltageSessionAction.ObserveOnly or CpuVoltageSessionAction.VoltageCycle
                ? response.RequestAccepted ? 0 : 2 : action == CpuVoltageSessionAction.Close && response.State != "Closed" ? 2 : 0;
        }
        catch (Exception exception) { output.WriteLine("Session request unavailable; do not replay a job. Use Status: " + exception.Message); return 3; }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
}
