namespace OpenBlade.Protocol;

internal sealed class NativeDeviceReadException : IOException
{
    internal NativeDeviceReadException(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic,
        DeviceStateQuery? stateQuery,
        Task? nativeRetirement = null)
        : base("Device-state query failed closed.")
    {
        Failure = failure;
        Diagnostic = diagnostic;
        StateQuery = stateQuery;
        NativeRetirement = nativeRetirement;
    }

    internal HidSessionFailureKind Failure { get; }

    internal HidSessionFailureDiagnostic? Diagnostic { get; }

    internal DeviceStateQuery? StateQuery { get; }

    internal Task? NativeRetirement { get; }
}

internal sealed class NativeDeviceReadTimeoutException : TimeoutException
{
    internal NativeDeviceReadTimeoutException(
        HidSessionFailureDiagnostic? diagnostic,
        DeviceStateQuery? stateQuery,
        Task? nativeRetirement)
        : base("Battery state query timed out.")
    {
        Diagnostic = diagnostic;
        StateQuery = stateQuery;
        NativeRetirement = nativeRetirement;
    }

    internal HidSessionFailureKind Failure => HidSessionFailureKind.Timeout;

    internal HidSessionFailureDiagnostic? Diagnostic { get; }

    internal DeviceStateQuery? StateQuery { get; }

    internal Task? NativeRetirement { get; }
}
