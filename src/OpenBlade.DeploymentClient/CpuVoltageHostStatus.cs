using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentClient;

internal static class DeploymentClientArguments
{
    internal static bool TryParse(string[] args, out bool cpuVoltage)
    {
        cpuVoltage = args is ["--cpu-voltage"];
        return args.Length == 0 || cpuVoltage;
    }
}

internal interface ICpuVoltageHostStatusVerifier
{
    Task<bool> PrepareAsync(CancellationToken cancellationToken);
    Task<bool> VerifyAsync(Guid deploymentRequestId, CancellationToken cancellationToken);
}

internal sealed class WindowsCpuVoltageHostStatusVerifier(Action<string>? diagnostic = null)
    : ICpuVoltageHostStatusVerifier
{
    internal const int MaximumReplyBytes = 512 * 1024;
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<bool> PrepareAsync(CancellationToken cancellationToken)
    {
        CpuVoltageHostEnrollment? enrollment = CpuVoltageHostState.ReadActive();
        if (enrollment is null) { return true; }
        RequireCurrentUser(enrollment);
        string closed = Path.Combine(CpuVoltageHostState.Root, enrollment.Session.ToString("N"), "closed.json");
        try
        {
            _ = CpuVoltageHostState.ReadProtected(closed);
            CpuVoltageHostState.RequireClosedAndTerminal(enrollment);
            return true;
        }
        catch (FileNotFoundException) { }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        using JsonDocument status = await RequestAsync(enrollment, close: false, deadline.Token).ConfigureAwait(false);
        RequireReply(status.RootElement, enrollment.Session, closed: false, allowBlockedIdle: true);
        RequireCurrentUser(enrollment);
        using JsonDocument reply = await RequestAsync(enrollment, close: true, deadline.Token).ConfigureAwait(false);
        RequireReply(reply.RootElement, enrollment.Session, closed: true);
        while (CpuVoltageHostState.IsExactProcessRunning(enrollment))
        {
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
        deadline.Token.ThrowIfCancellationRequested();
        CpuVoltageHostState.RequireClosedAndTerminal(enrollment);
        return true;
    }

    public async Task<bool> VerifyAsync(Guid deploymentRequestId, CancellationToken cancellationToken)
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade", "LocalDevDeploymentBroker", "staging", deploymentRequestId.ToString("N"),
            "cpu-voltage", "OpenBlade.Capture.exe");
        CpuVoltageHostEnrollment enrollment = await WaitForEnrollmentAsync(expected,
            CpuVoltageHostState.ReadActive, RequireCurrentUser, CpuVoltageHostState.RequireClosedAndTerminal,
            Task.Delay, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        using JsonDocument status = await RequestAsync(enrollment, close: false, deadline.Token).ConfigureAwait(false);
        RequireReply(status.RootElement, enrollment.Session, closed: false);
        RequireCurrentUser(enrollment);
        if (CpuVoltageHostState.ReadActive() != enrollment || !CpuVoltageHostState.IsExactProcessRunning(enrollment))
        { throw new IOException("CPU voltage host changed during status verification."); }
        diagnostic?.Invoke($"cpu-voltage-host-status:Ready:{enrollment.Session:N}");
        return true;
    }

    internal static async Task<CpuVoltageHostEnrollment> WaitForEnrollmentAsync(
        string expectedImage, Func<CpuVoltageHostEnrollment?> readActive,
        Action<CpuVoltageHostEnrollment> requireCurrentUser, Action<CpuVoltageHostEnrollment> requireClosed,
        Func<TimeSpan, CancellationToken, Task> delay, TimeProvider clock, CancellationToken cancellationToken)
    {
        long started = clock.GetTimestamp();
        CpuVoltageHostEnrollment? previous = null;
        while (clock.GetElapsedTime(started) < StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CpuVoltageHostEnrollment? value = readActive();
            if (value is not null)
            {
                requireCurrentUser(value);
                if (string.Equals(value.HostImage, expectedImage, StringComparison.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.GetElapsedTime(started) >= StartupTimeout) { break; }
                    return value;
                }
                if (previous is not null && previous != value)
                { throw new IOException("An unrelated CPU voltage host appeared during startup."); }
                requireClosed(value);
                previous = value;
            }
            else if (previous is not null)
            { throw new IOException("The protected CPU voltage enrollment disappeared during startup."); }
            TimeSpan remaining = StartupTimeout - clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { break; }
            await delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100),
                cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("CPU voltage host did not publish its matching enrollment within 60 seconds.");
    }

    private static void RequireCurrentUser(CpuVoltageHostEnrollment enrollment)
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        using Process process = Process.GetCurrentProcess();
        if (current.User?.Value != enrollment.UserSid || process.SessionId != enrollment.WindowsSession
            || new WindowsClientIntegritySource().IsElevated()
            || !new WindowsClientSessionSource().IsActiveLocalConsoleSession())
        { throw new UnauthorizedAccessException("CPU voltage enrollment does not match the active normal user."); }
    }

    // Only these two fixed actions are representable; no CPU job can be submitted here.
    internal static byte[] CreateRequest(Guid requestId, bool close) => JsonSerializer.SerializeToUtf8Bytes(new
    { version = 1, requestId, action = close ? "Close" : "Status" });

    internal static void RequireReply(JsonElement reply, Guid session, bool closed, bool allowBlockedIdle = false)
    {
        if (reply.ValueKind != JsonValueKind.Object
            || !reply.TryGetProperty("session", out JsonElement identity)
            || identity.ValueKind != JsonValueKind.String || !identity.TryGetGuid(out Guid actualSession) || actualSession != session
            || !reply.TryGetProperty("state", out JsonElement state) || state.ValueKind != JsonValueKind.String
            || (state.GetString() != (closed ? "Closed" : "Ready")
                && !(allowBlockedIdle && !closed && state.GetString() == "Blocked"))
            || !reply.TryGetProperty("job", out JsonElement job) || job.ValueKind != JsonValueKind.Null
            || !reply.TryGetProperty("requestAccepted", out JsonElement accepted) || accepted.ValueKind != JsonValueKind.False)
        { throw new InvalidDataException("The CPU voltage host did not confirm an idle matching session."); }
    }

    private static async Task<JsonDocument> RequestAsync(
        CpuVoltageHostEnrollment enrollment, bool close, CancellationToken cancellationToken)
    {
        RequireCurrentUser(enrollment);
        using SafeProcessHandle retained = OpenProcess(0x00101000, false, enrollment.HostPid);
        if (retained.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!CpuVoltageHostState.IsExactProcessRunning(enrollment)) { throw new IOException("CPU voltage host is not running."); }
        using var pipe = new NamedPipeClientStream(".", CpuVoltageHostState.PipeName(enrollment.Session),
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != enrollment.HostPid
            || !CpuVoltageHostState.IsExactProcessRunning(enrollment) || CpuVoltageHostState.ReadActive() != enrollment)
        { throw new IOException("CPU voltage pipe peer differs from its protected enrollment."); }
        RequireCurrentUser(enrollment);
        byte[] bytes = CreateRequest(Guid.NewGuid(), close);
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        await pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumReplyBytes) { throw new InvalidDataException("CPU voltage Status exceeds its bound."); }
        byte[] reply = new byte[length];
        await pipe.ReadExactlyAsync(reply, cancellationToken).ConfigureAwait(false);
        try { return JsonDocument.Parse(reply); }
        catch (JsonException exception) { throw new InvalidDataException("CPU voltage Status is malformed.", exception); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
