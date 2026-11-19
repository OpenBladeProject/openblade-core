namespace OpenBlade.Protocol;

internal static class CpuVoltageModelIdentity
{
    internal static HostIdentityReadResult ReadHost(CpuLiveCurveContract contract)
    {
        var registry = new WindowsRegistryValueSource();
        // Model-scoped admission treats SKU and BIOS as provenance only.
        return new WindowsHostIdentitySource(registry, new RuntimeWindowsPlatformEvidenceSource(registry),
            new ExpectedHostIdentity("Razer", contract.ProductName, contract.ModelNumber,
                Array.Empty<string>(), FirmwareAdmissionScope.ModelScoped)).ReadExactIdentity();
    }

    internal static DeviceIdentity? ReadIdentity()
    {
        foreach (var contract in CpuLiveCurveContract.All)
        {
            var host = ReadHost(contract);
            if (host is { Failure: null, Evidence: { } evidence })
            { return new(contract.ModelNumber, 0x1532, contract.ProductId, evidence.BiosVersion ?? "Unknown"); }
        }
        return null;
    }
}
