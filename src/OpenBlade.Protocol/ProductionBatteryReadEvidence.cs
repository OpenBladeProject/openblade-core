namespace OpenBlade.Protocol;

internal enum ProductionBatteryQueryCapability
{
    TemporaryFullCharge,
}

internal sealed class ProductionBatteryReadEvidence
{
    internal ProductionBatteryReadEvidence(
        IReadOnlySet<ProductionBatteryQueryCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        SupportsTemporaryFullChargeQuery = capabilities.Contains(
            ProductionBatteryQueryCapability.TemporaryFullCharge);
    }

    public static ProductionBatteryReadEvidence Production { get; } = new(
        new HashSet<ProductionBatteryQueryCapability>());

    public bool SupportsTemporaryFullChargeQuery { get; }
}
