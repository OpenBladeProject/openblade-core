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
    Rz090581PowerModeControlBitmask,
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
        DecodesPowerModeControlBitmask = capabilities.Contains(
            ProductionBatteryQueryCapability.Rz090581PowerModeControlBitmask);
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
            ProductionBatteryQueryCapability.Rz090581PowerModeControlBitmask,
        });

    public bool SupportsTemporaryFullChargeQuery { get; }

    public bool SupportsAdapterPowerQuery { get; }

    public bool SupportsMcuVersionQuery { get; }

    public bool SupportsStartupAnimationQuery { get; }

    public bool SupportsFunctionKeyPrimaryQuery { get; }

    public bool SupportsGamingModeQuery { get; }

    public bool SupportsRz090581CapabilityDiagnosticsQuery { get; }

    /// <summary>
    /// Exact RZ09-0581 only: 07/8F is a power-mode-control bitmask. Any subset of
    /// 0x0F decodes, and 07/0F writes require the non-0x04 bits to be clear.
    /// </summary>
    public bool DecodesPowerModeControlBitmask { get; }
}
