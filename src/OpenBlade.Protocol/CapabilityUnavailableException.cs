namespace OpenBlade.Protocol;

/// <summary>
/// Refuses an operation that the device's declared capabilities do not support.
/// Callers must use <see cref="OpenBlade.Contracts.DeviceCapabilities"/> for
/// discovery; this exception is a safety backstop for incorrect call paths.
/// </summary>
public sealed class CapabilityUnavailableException : InvalidOperationException
{
    public CapabilityUnavailableException(string message)
        : base(message)
    {
    }
}
