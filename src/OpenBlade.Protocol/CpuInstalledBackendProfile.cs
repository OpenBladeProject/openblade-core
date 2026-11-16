namespace OpenBlade.Protocol;

// Exact reverse-engineered driver recipes. These raw IOCTL contracts are not a
// public compatible API: all three retained files must match one reviewed SDK.
internal sealed record CpuInstalledBackendProfile(CpuLiveCurveContract Contract,
    string ServiceName, string DriverPath, string SignerThumbprint,
    IReadOnlyDictionary<string, string> ExpectedHashes)
{
    internal const string DirectoryPath = Rz090528InstalledCpuBackendInspector.DirectoryPath;
    internal static CpuInstalledBackendProfile ForContract(CpuLiveCurveContract contract)
    {
        if (contract == CpuLiveCurveContract.Rz090528)
        {
            return new(contract, Rz090528InstalledCpuBackendInspector.ServiceName,
                Rz090528InstalledCpuBackendInspector.DriverPath,
                Rz090528InstalledCpuBackendInspector.SignerThumbprint,
                Rz090528InstalledCpuBackendInspector.ExpectedHashes);
        }
        if (!contract.UsesPhoenixProtocol)
        { throw new ArgumentException("Unknown installed CPU backend contract.", nameof(contract)); }
        return new(contract, "AMDRyzenMasterDriverV22", DirectoryPath + @"\AMDRyzenMasterDriver.sys",
            "26F54EE84758BA716117321707BB08B3274A578A",
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [DirectoryPath + @"\AMDRyzenMasterDriver.sys"] = "78A5C1387A3BA460316E415C07A7E895175DB136D2B99D30218D421B06819FE0",
                    [DirectoryPath + @"\Platform.dll"] = "5ABC017C207EDB94CAF69DB5022FC0C95DD8B50E574669FEB788127663881664",
                    [DirectoryPath + @"\Device.dll"] = "57B4EA67B8DDF171B0084BD201C872811BE0E78130F3B9743F040454846FC683",
                }));
    }
}

// Resolve once only after positive exact-host evidence. A transient first read
// must not pin a different model for the lifetime of the service's native latch.
internal sealed class CpuInstalledBackendBinding(Func<DeviceIdentity?> readIdentity)
{
    private readonly object sync = new();
    private CpuInstalledBackendProfile? profile;
    internal CpuInstalledBackendProfile Get()
    {
        lock (sync)
        {
            if (profile is not null) { return profile; }
            var contract = CpuLiveCurveContract.ForIdentity(readIdentity());
            if (contract is null) { throw new CpuVoltageHostUnavailableException(); }
            return profile = CpuInstalledBackendProfile.ForContract(contract);
        }
    }
}

internal sealed class CpuVoltageHostUnavailableException : IOException
{
    internal CpuVoltageHostUnavailableException() : base("Exact CPU voltage host identity is unavailable.") { }
}
