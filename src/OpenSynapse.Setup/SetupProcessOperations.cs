using System.Diagnostics;

namespace OpenSynapse.Setup;

public interface ISetupProcess : IDisposable
{
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
        public string? ExecutablePath => process.MainModule?.FileName;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);

        public void Dispose() => process.Dispose();
    }
}
