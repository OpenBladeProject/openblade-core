using System.Globalization;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal static class NativeFailureDescription
{
    internal static string Describe(Exception exception)
    {
        HidSessionFailureDiagnostic? diagnostic = null;
        DeviceStateQuery? query = null;
        byte? commandClass = null;
        byte? commandId = null;
        HidSessionFailureKind? failure = null;
        if (exception is BladeWriteException write)
        {
            diagnostic = write.Diagnostic;
            query = write.StateQuery;
            commandClass = write.CommandClass;
            commandId = write.CommandId;
        }
        else if (exception is NativeDeviceReadException read)
        {
            diagnostic = read.Diagnostic;
            query = read.StateQuery;
            failure = read.Failure;
        }
        if (diagnostic is null && query is null && commandClass is null)
        {
            return exception.Message;
        }
        string identity = query is { } stateQuery
            ? $", query={stateQuery}"
            : commandClass is { } category && commandId is { } command
                ? $", command={category:X2}/{command:X2}"
                : string.Empty;
        string nativeError = diagnostic?.NativeWin32Error?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        string bytes = diagnostic?.TransferredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        return $"{exception.Message} (failure={diagnostic?.Failure ?? failure}, phase={diagnostic?.Phase.ToString() ?? "unavailable"}{identity}, nativeError={nativeError}, bytes={bytes}).";
    }
}
