using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Capture;

// The pipe peer binds the exact retained kernel process object. An acknowledgement
// is insufficient: driver cleanup also requires that same object to be terminal.
internal sealed class WindowsCpuVoltageServiceProcess
{
    internal const string Image = @"C:\Program Files\OpenBlade\OpenBlade.Service.exe";
    private readonly ICpuPmRetainedProcessProbe process;
    private readonly CpuPmProcessObservation initial;
    private readonly ICpuPmLaunchFiles pins;
    private readonly string version;
    private readonly Func<int[]>? processInventory;
    private bool shutdownRequested;
    internal CpuPmProcessObservation Identity => initial;
    internal void ReleaseAfterTerminal() { AssertState(CpuPmChildState.Terminal); process.Dispose(); }
    internal WindowsCpuVoltageServiceProcess(ICpuPmRetainedProcessProbe process, CpuPmProcessObservation initial,
        ICpuPmLaunchFiles pins, string version, Func<int[]>? processInventory = null)
    { this.process = process; this.initial = initial; this.pins = pins; this.version = version; this.processInventory = processInventory; }
    internal static WindowsCpuVoltageServiceProcess Bind(ICpuPmLaunchFiles pins, string version, CancellationToken token,
        Action<WindowsCpuVoltageServiceProcess>? retainBeforeVersion = null)
    {
        using var pipe = Connect(token, startup: retainBeforeVersion is not null);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid is 0 or > int.MaxValue)
        { throw new IOException("Service pipe identity unavailable."); }
        using var source = Process.GetProcessById(checked((int)pid));
        var retained = WindowsCpuPmProcessProbe.FromLaunchedHandle(source.SafeHandle);
        var observed = retained.Read(true);
        if (observed.Pid != pid || observed.State != CpuPmChildState.Running || observed.Image != Image)
        { throw new IOException("Exact installed Service process required."); }
        var value = new WindowsCpuVoltageServiceProcess(retained, observed, pins, version);
        retainBeforeVersion?.Invoke(value);
        value.AssertState(CpuPmChildState.Running);
        value.VerifyVersion(token);
        return value;
    }
    internal void AssertState(CpuPmChildState expected)
    {
        pins.AssertAllPins(); AssertRetainedState(expected);
        // A replacement instance cannot inherit the old process's terminal proof.
        int[] processes = processInventory is null ? ReadProcessIds() : processInventory();
        if (expected == CpuPmChildState.Running ? processes.Length != 1 || processes[0] != initial.Pid
            : processes.Length != 0) { throw new IOException("Service process inventory changed."); }
        pins.AssertAllPins();
    }
    private void AssertRetainedState(CpuPmChildState expected)
    {
        var value = process.Read(true);
        if (value.Pid != initial.Pid || value.Created != initial.Created || value.State != expected
            || expected == CpuPmChildState.Running && value.Image != Image)
        { throw new IOException("Retained Service identity or state changed."); }
    }
    private static int[] ReadProcessIds()
    {
        var values = Process.GetProcessesByName("OpenBlade.Service");
        try { return values.Select(value => value.Id).ToArray(); }
        finally { foreach (var value in values) { value.Dispose(); } }
    }
    internal CpuPmChildState ReadState()
    {
        var value = process.Read(false);
        return value.Pid == initial.Pid && value.Created == initial.Created ? value.State : CpuPmChildState.Unknown;
    }
    private ValueTask<Stream> ConnectBound(CancellationToken token)
        => ConnectBoundCore(token, cancellation => Connect(cancellation), stream =>
        {
            if (!GetNamedPipeServerProcessId(((NamedPipeClientStream)stream).SafePipeHandle, out uint pid))
            { throw new IOException("Service IPC peer unavailable."); }
            return pid;
        });
    internal ValueTask<Stream> ConnectBoundCore(CancellationToken token, Func<CancellationToken, Stream> connect,
        Func<Stream, uint> serverPid)
    {
        AssertState(CpuPmChildState.Running);
        var pipe = connect(token);
        try
        {
            if (serverPid(pipe) != initial.Pid)
            { throw new IOException("Shutdown IPC peer differs from retained Service."); }
            // The Service's first-frame deadline is already running. Only the
            // exact retained kernel identity is checked here; full pins and
            // process inventory were checked before connection and are checked
            // again before accepting the typed response.
            AssertRetainedState(CpuPmChildState.Running); return ValueTask.FromResult<Stream>(pipe);
        }
        catch { pipe.Dispose(); throw; }
    }
    private void VerifyVersion(CancellationToken token, bool requireNoCpuRecovery = false)
    {
        var client = new ServiceClient(ConnectBound, TimeProvider.System);
        try
        {
            var status = client.GetStatusAsync(token).GetAwaiter().GetResult();
            AssertState(CpuPmChildState.Running);
            if (status.ServiceVersion != version) { throw new IOException("Service version differs from reviewed package."); }
            if (requireNoCpuRecovery) { RequireNoCpuRecovery(status.CpuTuning); }
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    internal static void RequireNoCpuRecovery(CpuTuningStatus? cpu)
    {
        if (cpu is not null && (cpu.RestorePending || cpu.LiveRecovery is { State: not CpuTuningRecoveryState.None }
            || cpu.FirmwareRecovery is { State: not CpuTuningRecoveryState.None }))
        { throw new IOException("Unrelated CPU recovery prevents resource-only cleanup."); }
    }
    internal void ShutdownOnce(CancellationToken token, bool requireNoCpuRecovery = false)
    {
        if (shutdownRequested) { throw new IOException("Shutdown already requested."); }
        VerifyVersion(token, requireNoCpuRecovery); shutdownRequested = true;
        var client = new ServiceClient(ConnectBound, TimeProvider.System);
        try
        {
            var result = client.ShutdownOpenBladeAsync(token).GetAwaiter().GetResult();
            AssertShutdownResponseState();
            if (result.Status != ControlOperationStatus.Stored) { throw new IOException("Typed Service shutdown unacknowledged."); }
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    internal void AssertShutdownResponseState()
    {
        // The same Service may exit after acknowledging shutdown. Both states
        // retain full pin and replacement-process validation before acceptance.
        CpuPmChildState state = ReadState();
        if (state is not (CpuPmChildState.Running or CpuPmChildState.Terminal))
        { throw new IOException("Shutdown Service state unavailable."); }
        AssertState(state);
    }
    internal static Task WaitForStartupPipeAsync(Func<int, CancellationToken, Task> connect, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return connect(15_000, token); // One bounded connection, inside the existing job deadline.
    }
    private static NamedPipeClientStream Connect(CancellationToken token, bool startup = false)
    {
        var pipe = new NamedPipeClientStream(".", OpenBladeIpc.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            (startup ? WaitForStartupPipeAsync(pipe.ConnectAsync, token) : pipe.ConnectAsync(2000, token)).GetAwaiter().GetResult();
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
    // Same Win32 peer API as WindowsCpuPmAuthorityChannel.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
