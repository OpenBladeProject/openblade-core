using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Setup;

public interface ISetupProcess : IDisposable
{
    int Id { get; }
    // False means the process is confirmed gone. Inspection or access failures
    // must throw, and a successful retention lasts until Dispose.
    bool TryRetainLifetime();
    string? ExecutablePath { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface ISetupProcessOperations
{
    IReadOnlyList<ISetupProcess> GetProcessesByName(string processName);
    ISetupProcess GetProcessById(int processId);
    bool TrySignalEvent(string eventName);
}

internal sealed class WindowsSetupProcessOperations : ISetupProcessOperations
{
    public IReadOnlyList<ISetupProcess> GetProcessesByName(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        try { return processes.Select(process => GetProcessById(process.Id)).ToArray(); }
        finally { foreach (Process process in processes) { process.Dispose(); } }
    }

    public ISetupProcess GetProcessById(int processId) =>
        new RetainedSetupProcess(processId, WindowsSetupProcessNativeOperations.Instance);

    public bool TrySignalEvent(string eventName)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? shutdownEvent))
        {
            return false;
        }

        using (shutdownEvent)
        {
            return shutdownEvent.Set();
        }
    }

}

// This seam tests that image inspection and waiting use the retained object,
// without enumerating or opening any real service processes in unit tests.
internal interface ISetupProcessNativeOperations
{
    SafeProcessHandle? Open(int processId, uint desiredAccess);
    bool HasExited(SafeProcessHandle handle);
    string ExecutablePath(SafeProcessHandle handle);
    Task WaitForExitAsync(SafeProcessHandle handle, CancellationToken cancellationToken);
}

internal sealed class RetainedSetupProcess(int processId, ISetupProcessNativeOperations native) : ISetupProcess
{
    private SafeProcessHandle? handle;
    private bool gone;
    private bool disposed;
    public int Id { get; } = processId > 0 ? processId : throw new ArgumentOutOfRangeException(nameof(processId));

    public bool TryRetainLifetime()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (gone) { return false; }
        handle ??= native.Open(Id, WindowsSetupProcessNativeOperations.QueryLimitedInformation | WindowsSetupProcessNativeOperations.Synchronize);
        if (handle is null || native.HasExited(handle))
        {
            gone = true;
            return false;
        }
        return true;
    }

    public string? ExecutablePath => TryRetainLifetime() ? native.ExecutablePath(handle!) : null;

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        TryRetainLifetime() ? native.WaitForExitAsync(handle!, cancellationToken) : Task.CompletedTask;

    public void Dispose()
    {
        disposed = true;
        handle?.Dispose();
    }
}

internal sealed class WindowsSetupProcessNativeOperations : ISetupProcessNativeOperations
{
    internal static readonly WindowsSetupProcessNativeOperations Instance = new();
    internal const uint QueryLimitedInformation = 0x1000;
    internal const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 258;

    public SafeProcessHandle? Open(int processId, uint desiredAccess)
    {
        // No VM_READ, termination or all-access rights are needed for identity
        // and lifetime observation. Never reopen this PID after retaining it.
        SafeProcessHandle handle = OpenProcess(desiredAccess, false, processId);
        if (!handle.IsInvalid) { return handle; }
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == 87) { return null; } // Positive PID no longer exists.
        throw new Win32Exception(error);
    }

    public bool HasExited(SafeProcessHandle handle) => WaitForSingleObject(handle, 0) switch
    {
        0 => true,
        WaitTimeout => false,
        _ => throw new Win32Exception(Marshal.GetLastWin32Error()),
    };

    public string ExecutablePath(SafeProcessHandle handle)
    {
        StringBuilder image = new(32768);
        int length = image.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, image, ref length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return image.ToString();
    }

    public async Task WaitForExitAsync(SafeProcessHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The registration owns a separate wait handle to the same process
        // object. Cancellation may dispose the identity handle without racing
        // an in-flight native wait or rebinding to a recycled numeric PID.
        nint current = GetCurrentProcess();
        if (!DuplicateHandle(current, handle, current, out SafeWaitHandle duplicate, Synchronize, false, 0))
        {
            int error = Marshal.GetLastWin32Error();
            duplicate?.Dispose();
            throw new Win32Exception(error);
        }
        using ProcessExitWaitHandle wait = new(duplicate);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(wait,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), completion,
            Timeout.Infinite, executeOnlyOnce: true);
        using CancellationTokenRegistration cancellation = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try { await completion.Task.ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }

    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        // RegisteredWaitHandle retains this owned duplicate until its native
        // wait has been unregistered, including cancellation races.
        internal ProcessExitWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, StringBuilder image, ref int length);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, SafeProcessHandle source, nint targetProcess,
        out SafeWaitHandle target, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
}
