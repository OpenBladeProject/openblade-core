using System.Diagnostics;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal static class SessionAgentLauncher
{
    internal static bool TryStartSibling()
    {
        string executable = Path.Combine(
            AppContext.BaseDirectory,
            "OpenBlade.SessionAgent.exe");
        if (!File.Exists(executable))
        {
            return false;
        }

        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        return process is not null;
    }

    internal static bool TryStopSibling()
    {
        string executable = Path.Combine(
            AppContext.BaseDirectory,
            "OpenBlade.SessionAgent.exe");
        try
        {
            using EventWaitHandle shutdownEvent = EventWaitHandle.OpenExisting(
                SessionAgentShutdownEndpoint.ForExecutablePath(executable));
            return shutdownEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}
