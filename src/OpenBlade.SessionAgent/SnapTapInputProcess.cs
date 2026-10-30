using System.Diagnostics;
using System.Globalization;
using OpenBlade.Tray;

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

    internal static string StopEventName(int parentId, KeyboardInputBackend backend = KeyboardInputBackend.RazerDriver) =>
        $"Local\\OpenBlade.SnapTap.Stop.{parentId.ToString(CultureInfo.InvariantCulture)}.{SnapTapInputHost.BackendArgument(backend)}";

    internal static ISnapTapChildProcess Start() => Start(KeyboardInputBackend.RazerDriver);

    internal static ISnapTapChildProcess Start(KeyboardInputBackend backend) => Start(backend, KeyboardDriverFeatures.SnapTap);

    internal static ISnapTapChildProcess Start(KeyboardInputBackend backend, KeyboardDriverFeatures features)
    {
        string argument = SnapTapInputHost.BackendArgument(backend);
        string featureArgument = SnapTapInputHost.FeaturesArgument(features);
        if (backend == KeyboardInputBackend.WindowsHook && features != KeyboardDriverFeatures.SnapTap)
        {
            throw new ArgumentOutOfRangeException(nameof(features));
        }
        string path = Environment.ProcessPath
            ?? throw new InvalidOperationException("The input helper path is unavailable.");
        var stopEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            StopEventName(Environment.ProcessId, backend));
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
            start.ArgumentList.Add(argument);
            if (backend == KeyboardInputBackend.RazerDriver) { start.ArgumentList.Add(featureArgument); }
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
