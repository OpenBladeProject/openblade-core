namespace OpenBlade.Protocol;

internal enum ProductionBatteryQueryCapability
{
    TemporaryFullCharge,
    AdapterPower,
    McuVersion,
    StartupAnimation,
    FunctionKeyPrimary,
    GamingMode,
    Rz090581CapabilityDiagnostics,
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
        SupportsGamingModeQuery = capabilities.Contains(
            ProductionBatteryQueryCapability.GamingMode);
        SupportsRz090581CapabilityDiagnosticsQuery = capabilities.Contains(
            ProductionBatteryQueryCapability.Rz090581CapabilityDiagnostics);
    }

    public static ProductionBatteryReadEvidence Production { get; } = new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.TemporaryFullCharge,
            ProductionBatteryQueryCapability.AdapterPower,
            ProductionBatteryQueryCapability.McuVersion,
            ProductionBatteryQueryCapability.StartupAnimation,
            ProductionBatteryQueryCapability.FunctionKeyPrimary,
            ProductionBatteryQueryCapability.GamingMode,
        });

    internal static ProductionBatteryReadEvidence Rz090581ModelScoped { get; } = new(
        new HashSet<ProductionBatteryQueryCapability>
        {
            ProductionBatteryQueryCapability.TemporaryFullCharge,
            ProductionBatteryQueryCapability.AdapterPower,
            ProductionBatteryQueryCapability.McuVersion,
            ProductionBatteryQueryCapability.StartupAnimation,
            ProductionBatteryQueryCapability.FunctionKeyPrimary,
            ProductionBatteryQueryCapability.GamingMode,
            ProductionBatteryQueryCapability.Rz090581CapabilityDiagnostics,
        });

    public bool SupportsTemporaryFullChargeQuery { get; }

    public bool SupportsAdapterPowerQuery { get; }

    public bool SupportsMcuVersionQuery { get; }

    public bool SupportsStartupAnimationQuery { get; }

    public bool SupportsFunctionKeyPrimaryQuery { get; }

    public bool SupportsGamingModeQuery { get; }

    public bool SupportsRz090581CapabilityDiagnosticsQuery { get; }
}
