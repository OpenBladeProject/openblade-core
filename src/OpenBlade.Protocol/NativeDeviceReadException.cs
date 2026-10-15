namespace OpenBlade.Protocol;

internal sealed class NativeDeviceReadException : IOException
{
    internal NativeDeviceReadException(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic,
        DeviceStateQuery? stateQuery)
        : base("Device-state query failed closed.")
    {
        Failure = failure;
        Diagnostic = diagnostic;
        StateQuery = stateQuery;
    }

    internal HidSessionFailureKind Failure { get; }

    internal HidSessionFailureDiagnostic? Diagnostic { get; }

    internal DeviceStateQuery? StateQuery { get; }
}
