using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// The guardian owns the delegated HID query lane. This factory has no setter
// fixtures and admits only 07/8C, independently established for each model in
// docs/synapse-adapter-wattage.md, docs/source-reviewed-blade-models.md and
// their existing production read evidence.
internal static class FanStopAdapterPowerReader
{
    private static readonly SemaphoreSlim Rz090483QueryGate = new(1, 1);
    private static readonly SemaphoreSlim Rz090510QueryGate = new(1, 1);
    private static readonly SemaphoreSlim Rz090530QueryGate = new(1, 1);
    private static readonly SemaphoreSlim Rz090421QueryGate = new(1, 1);
    private static readonly SemaphoreSlim Rz090427QueryGate = new(1, 1);
    private static readonly SemaphoreSlim Rz090330QueryGate = new(1, 1);
    internal static async Task<AdapterPowerState> ReadAsync(FanStopModelContract contract,
        CancellationToken cancellationToken)
    {
        var registry = new WindowsRegistryValueSource();
        var native = new WindowsHidNative();
        var guard = new WindowsHardwareAccessGuard(new RunningProcessSource(), new WindowsServiceSource());
        var host = new WindowsHostIdentitySource(registry, new RuntimeWindowsPlatformEvidenceSource(registry), contract.Host);
        var opener = new WindowsRazerHidSessionOpener(host, guard, native,
            ControllerExclusivityPolicy.Strict, contract.ControlHid);
        IProductionBatteryPortFactory factory = CreateFactory(contract, settleDelay => new ValidationActorFactory(
            opener, new OverlappedFeatureOperation(native), guard, TimeProvider.System,
            TimeSpan.FromSeconds(3), ControllerExclusivityPolicy.Strict, settleDelay));
        var opened = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (opened.Value is not { } port) { throw new IOException("Independent adapter power open failed."); }
        await using (port.ConfigureAwait(false))
        {
            if (opened.Failure is not null) { throw new IOException("Independent adapter power open is unresolved."); }
            return await port.QueryAdapterPowerStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static ProductionBatteryFactory CreateFactory(FanStopModelContract contract,
        Func<TimeSpan, IValidationActorFactory> createActorFactory)
    {
        if (!FanStopModelContract.All.Contains(contract))
        { throw new UnvalidatedCommandException("The exact fan-stop model contract is unavailable."); }
        return new ProductionBatteryFactory(DeviceCommandCatalog.Empty,
            new ProductionBatteryReadEvidence(new HashSet<ProductionBatteryQueryCapability>
            { ProductionBatteryQueryCapability.AdapterPower }),
            static () => new ValidationRunContext(), createActorFactory, TimeProvider.System,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), PendingDisposalRegistry.Shared,
            admittedAdapterPowerStates: AdapterPowerProtocol.SynapseAdapterStates,
            sharedCommandGate: contract.Model switch
            {
                "RZ09-0581" => Rz090581FeatureCommandGate.Shared,
                "RZ09-0528" => Rz090528FeatureCommandGate.Shared,
                "RZ09-0483" => Rz090483QueryGate,
                "RZ09-0510" => Rz090510QueryGate,
                "RZ09-0530" => Rz090530QueryGate,
                "RZ09-0421" => Rz090421QueryGate,
                "RZ09-0427" => Rz090427QueryGate,
                "RZ09-0330" => Rz090330QueryGate,
                _ => throw new UnvalidatedCommandException("The fan-stop adapter query is not reviewed."),
            });
    }
}
