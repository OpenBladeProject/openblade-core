using System.Diagnostics;

namespace OpenBlade.Setup;

public interface ISetupProcess : IDisposable
{
    // False means the process is confirmed gone. Inspection or access failures
    // must throw, and a successful retention lasts until Dispose.
    bool TryRetainLifetime();
    string? ExecutablePath { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface ISetupProcessOperations
{
    IReadOnlyList<ISetupProcess> GetProcessesByName(string processName);
    bool TrySignalEvent(string eventName);
}

internal sealed class WindowsSetupProcessOperations : ISetupProcessOperations
{
    public IReadOnlyList<ISetupProcess> GetProcessesByName(string processName) =>
        Process.GetProcessesByName(processName)
            .Select(process => (ISetupProcess)new WindowsSetupProcess(process))
            .ToArray();

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

    private sealed class WindowsSetupProcess(Process process) : ISetupProcess
    {
        public bool TryRetainLifetime()
        {
            try
            {
                // Enumeration defers opening a handle. Retain the OS process
                // object before identity inspection and shutdown, preventing PID reuse.
                _ = process.SafeHandle;
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false; // The enumerated process already exited.
            }
            catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 87)
            {
                return false; // ERROR_INVALID_PARAMETER: the PID no longer exists.
            }
        }

        public string? ExecutablePath => process.MainModule?.FileName;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);

        public void Dispose() => process.Dispose();
    }
}
