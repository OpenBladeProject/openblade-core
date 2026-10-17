using System.Diagnostics;
using System.Globalization;

namespace OpenBlade.SessionAgent;

internal sealed class SnapTapInputProcess : ISnapTapChildProcess
{
    private readonly Process process;
    private readonly EventWaitHandle stopEvent;

    private SnapTapInputProcess(Process process, EventWaitHandle stopEvent)
    {
        this.process = process;
        this.stopEvent = stopEvent;
    }

    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;

    internal static string StopEventName(int parentId) =>
        $"Local\\OpenBlade.SnapTap.Stop.{parentId.ToString(CultureInfo.InvariantCulture)}";

    internal static ISnapTapChildProcess Start()
    {
        string path = Environment.ProcessPath
            ?? throw new InvalidOperationException("The input helper path is unavailable.");
        var stopEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            StopEventName(Environment.ProcessId));
        try
        {
            stopEvent.Reset();
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            };
            start.ArgumentList.Add(SnapTapInputHost.ModeArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process process = Process.Start(start)
                ?? throw new InvalidOperationException("The input helper could not start.");
            return new SnapTapInputProcess(process, stopEvent);
        }
        catch
        {
            stopEvent.Dispose();
            throw;
        }
    }

    public void RequestStop() => stopEvent.Set();

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    public void Dispose()
    {
        process.Dispose();
        stopEvent.Dispose();
    }
}
