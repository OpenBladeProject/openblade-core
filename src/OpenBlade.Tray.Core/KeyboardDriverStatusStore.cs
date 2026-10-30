using System.Diagnostics;

namespace OpenBlade.Tray;

[Flags]
public enum KeyboardDriverFeatures
{
    None = 0,
    SnapTap = 1,
    FunctionRow = 2,
}

/// <summary>One driver subscription owns the selected feature union.</summary>
public static class KeyboardDriverStatusStore
{
    private static int scope = (int)KeyboardDriverFeatures.SnapTap;
    private static int functionKeyKnown;
    public static bool FunctionKeyKnown => Volatile.Read(ref functionKeyKnown) != 0;
    public static void SetFunctionKeyKnown(bool known) => Volatile.Write(ref functionKeyKnown, known ? 1 : 0);

    public static void ConfigureScope(KeyboardDriverFeatures features)
    {
        Validate(features);
        Volatile.Write(ref scope, (int)features);
    }

    public static string GetDefaultPath()
    {
        using Process process = Process.GetCurrentProcess();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenBlade", $"keyboard-driver-status-{process.SessionId}.json");
    }

    public static SnapTapRuntimeStatus? Read(string? path = null) =>
        SnapTapStatusStore.Read(path ?? GetDefaultPath());

    public static SnapTapDiagnosticReport CaptureDiagnostics(string? path = null) =>
        SnapTapStatusStore.CaptureDiagnostics(path ?? GetDefaultPath());

    public static void Write(SnapTapRuntimeStatus status) =>
        Write(status, (KeyboardDriverFeatures)Volatile.Read(ref scope));

    public static void Write(SnapTapRuntimeStatus status, KeyboardDriverFeatures features,
        string? jointPath = null, string? snapTapPath = null, string? functionRowPath = null)
    {
        Validate(features);
        status = status with { Backend = KeyboardInputBackend.RazerDriver };
        SnapTapStatusStore.Write(status, jointPath ?? GetDefaultPath());
        if ((features & KeyboardDriverFeatures.SnapTap) != 0)
        {
            SnapTapStatusStore.Write(status, snapTapPath);
        }
        if ((features & KeyboardDriverFeatures.FunctionRow) != 0)
        {
            FunctionRowBackendStatusStore.Write(ProjectFunctionRow(status, FunctionKeyKnown), functionRowPath);
        }
    }

    public static SnapTapRuntimeStatus ProjectFunctionRow(SnapTapRuntimeStatus status, bool functionKeyKnown) => status with
    {
        ExperimentalDriver = true,
        State = !functionKeyKnown && status.State is SnapTapRuntimeState.Armed or SnapTapRuntimeState.Active
            ? SnapTapRuntimeState.WaitingForFunctionKey : status.State,
    };

    private static void Validate(KeyboardDriverFeatures features)
    {
        if ((features & ~(KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(features));
        }
    }
}
