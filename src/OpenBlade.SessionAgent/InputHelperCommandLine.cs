using System.Globalization;

namespace OpenBlade.ProcessArguments;

internal enum InputHelperMode { Input, DriverWorker }
internal enum InputHelperBackend { WindowsHook, RazerDriver }
[Flags]
internal enum InputHelperFeatures { None = 0, SnapTap = 1, FunctionRow = 2 }

internal readonly record struct InputHelperArguments(
    InputHelperMode Mode, int ParentProcessId, InputHelperBackend Backend, InputHelperFeatures Features);

// Source-linked into the deployment client: process-family admission must use
// the same bounded argument grammar as the helper that will consume it.
internal static class InputHelperCommandLine
{
    internal const string InputMode = "--snap-tap-input";
    internal const string WorkerMode = "--snap-tap-driver-worker";

    internal static bool IsRequested(IReadOnlyList<string> args, int startIndex = 0) =>
        startIndex >= 0 && startIndex < args.Count && args[startIndex] is InputMode or WorkerMode;

    internal static string BackendArgument(InputHelperBackend backend) => backend switch
    {
        InputHelperBackend.WindowsHook => "windows-hook",
        InputHelperBackend.RazerDriver => "razer-driver",
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    internal static string FeaturesArgument(InputHelperFeatures features) => features switch
    {
        InputHelperFeatures.SnapTap => "snap-tap",
        InputHelperFeatures.FunctionRow => "function-row",
        InputHelperFeatures.SnapTap | InputHelperFeatures.FunctionRow => "snap-tap-and-function-row",
        _ => throw new ArgumentOutOfRangeException(nameof(features)),
    };

    internal static bool TryParse(IReadOnlyList<string> args, out InputHelperArguments parsed, int startIndex = 0)
    {
        parsed = default;
        if (startIndex < 0 || startIndex > args.Count) { return false; }
        int count = args.Count - startIndex;
        if (count is < 2 or > 4 || !IsRequested(args, startIndex)
            || !int.TryParse(args[startIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent)
            || parent <= 0)
        {
            return false;
        }

        InputHelperFeatures features = InputHelperFeatures.SnapTap;
        InputHelperBackend backend = InputHelperBackend.RazerDriver;
        bool worker = args[startIndex] == WorkerMode;
        if (worker)
        {
            if (count > 3) { return false; }
            if (count == 3) { features = ParseFeatures(args[startIndex + 2]); }
        }
        else
        {
            if (count >= 3)
            {
                if (args[startIndex + 2] == "windows-hook") { backend = InputHelperBackend.WindowsHook; }
                else if (args[startIndex + 2] != "razer-driver") { return false; }
            }
            if (count == 4)
            {
                if (backend != InputHelperBackend.RazerDriver) { return false; }
                features = ParseFeatures(args[startIndex + 3]);
            }
        }
        if (features == InputHelperFeatures.None) { return false; }
        parsed = new(worker ? InputHelperMode.DriverWorker : InputHelperMode.Input, parent, backend, features);
        return true;
    }

    private static InputHelperFeatures ParseFeatures(string value) => value switch
    {
        "snap-tap" => InputHelperFeatures.SnapTap,
        "function-row" => InputHelperFeatures.FunctionRow,
        "snap-tap-and-function-row" => InputHelperFeatures.SnapTap | InputHelperFeatures.FunctionRow,
        _ => InputHelperFeatures.None,
    };
}
