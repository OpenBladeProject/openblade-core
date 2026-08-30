namespace OpenBlade.Service;

public enum ConfigurationLoadDisposition
{
    Admitted,
    Quarantined,
}

public enum ConfigurationQuarantineReason
{
    None,
    FutureSchema,
    InvalidDocument,
    IncompleteAggregate,
    HashMismatch,
    MixedSchema,
}

internal sealed class ConfigurationQuarantinedException(
    string message,
    ConfigurationQuarantineReason reason)
    : InvalidOperationException(message)
{
    internal ConfigurationQuarantineReason Reason { get; } = reason;
}
