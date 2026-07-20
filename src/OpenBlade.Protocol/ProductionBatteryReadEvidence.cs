namespace OpenBlade.Protocol;

internal enum ProductionBatteryQueryCapability
{
    TemporaryFullCharge,
    AdapterPower,
    McuVersion,
    StartupAnimation,
    FunctionKeyPrimary,
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
        SupportsMcuVersionQuery = capabilities.Contains(
            ProductionBatteryQueryCapability.McuVersion);
        SupportsStartupAnimationQuery = capabilities.Contains(
            ProductionBatteryQueryCapability.StartupAnimation);
        SupportsFunctionKeyPrimaryQuery = capabilities.Contains(
            ProductionBatteryQueryCapability.FunctionKeyPrimary);
    }

    public static ProductionBatteryReadEvidence Production { get; } = new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.TemporaryFullCharge,
            ProductionBatteryQueryCapability.AdapterPower,
            ProductionBatteryQueryCapability.McuVersion,
            ProductionBatteryQueryCapability.StartupAnimation,
            ProductionBatteryQueryCapability.FunctionKeyPrimary,
        });

    public bool SupportsTemporaryFullChargeQuery { get; }

    public bool SupportsAdapterPowerQuery { get; }

    public bool SupportsMcuVersionQuery { get; }

    public bool SupportsStartupAnimationQuery { get; }

    public bool SupportsFunctionKeyPrimaryQuery { get; }
}
