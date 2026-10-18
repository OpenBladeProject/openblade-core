using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal enum ScopedThermalFreshSessionRetryPolicy
{
    Allow,
    BlockDuringBoundedCleanup,
}

internal enum ScopedThermalAuthorityPurpose
{
    OrdinaryConfigurationIntent,
    PriorAuthorityCleanup,
}

internal sealed class ScopedThermalExecutionOptions
{
    private readonly IReadOnlyList<DeviceSubsystem> components;

    internal ScopedThermalExecutionOptions(
        IReadOnlyList<DeviceSubsystem> components,
        ScopedThermalFreshSessionRetryPolicy freshSessionRetryPolicy =
            ScopedThermalFreshSessionRetryPolicy.Allow,
        Func<string?>? additionalWriteBlockReason = null,
        ScopedThermalAuthorityPurpose authorityPurpose =
            ScopedThermalAuthorityPurpose.OrdinaryConfigurationIntent)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count == 0
            || components.Any(component => component is not (
                DeviceSubsystem.Performance or DeviceSubsystem.Fans))
            || components.Distinct().Count() != components.Count)
        {
            throw new ArgumentException(
                "Scoped thermal components must be a non-empty distinct set of performance and fan components.",
                nameof(components));
        }
        if (!Enum.IsDefined(freshSessionRetryPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(freshSessionRetryPolicy));
        }
        if (!Enum.IsDefined(authorityPurpose))
        {
            throw new ArgumentOutOfRangeException(nameof(authorityPurpose));
        }

        this.components = Array.AsReadOnly([.. components]);
        FreshSessionRetryPolicy = freshSessionRetryPolicy;
        AdditionalWriteBlockReason = additionalWriteBlockReason;
        AuthorityPurpose = authorityPurpose;
    }

    internal IReadOnlyList<DeviceSubsystem> Components => components;

    internal ScopedThermalFreshSessionRetryPolicy FreshSessionRetryPolicy { get; }

    internal Func<string?>? AdditionalWriteBlockReason { get; }

    internal ScopedThermalAuthorityPurpose AuthorityPurpose { get; }

    internal void ValidateFor(DeviceSubsystem subsystem)
    {
        bool valid = subsystem switch
        {
            DeviceSubsystem.Performance => components.Contains(DeviceSubsystem.Performance),
            DeviceSubsystem.Fans => components.Contains(DeviceSubsystem.Fans),
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"Scoped thermal execution options do not match the {subsystem} root.",
                nameof(subsystem));
        }
    }
}
