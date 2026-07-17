namespace OpenBlade.Protocol;

internal enum ProductionBatteryQueryCapability
{
    TemporaryFullCharge,
    AdapterPower,
}

internal sealed class ProductionBatteryReadEvidence
{
    internal ProductionBatteryReadEvidence(
        IReadOnlySet<ProductionBatteryQueryCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        SupportsTemporaryFullChargeQuery = capabilities.Contains(
            ProductionBatteryQueryCapability.TemporaryFullCharge);
        SupportsAdapterPowerQuery = capabilities.Contains(
            ProductionBatteryQueryCapability.AdapterPower);
    }

    public static ProductionBatteryReadEvidence Production { get; } = new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.TemporaryFullCharge,
            ProductionBatteryQueryCapability.AdapterPower,
        });

    public bool SupportsTemporaryFullChargeQuery { get; }

    public bool SupportsAdapterPowerQuery { get; }
}
