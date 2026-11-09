using System.Diagnostics;

namespace OpenBlade.Tray;

/// <summary>Function-row input has independent ownership and runtime status from SnapTap.</summary>
public static class FunctionRowBackendStatusStore
{
    public static string GetDefaultPath()
    {
        using Process process = Process.GetCurrentProcess();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenBlade",
            $"function-row-status-{process.SessionId}.json");
    }

    public static SnapTapRuntimeStatus? Read(string? path = null) =>
        SnapTapStatusStore.Read(path ?? GetDefaultPath());

    public static void Write(SnapTapRuntimeStatus status, string? path = null) =>
        SnapTapStatusStore.Write(status, path ?? GetDefaultPath());

    public static SnapTapDiagnosticReport CaptureDiagnostics(string? path = null) =>
        SnapTapStatusStore.CaptureDiagnostics(path ?? GetDefaultPath());
}
