using System.Security.Cryptography;
using System.Text;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// Each entry has its own firmware and updater review. Matching logical indices
// alone is insufficient: the host transport, completion, FE and Auto paths must
// all implement the same bounded contract before adding another entry.
internal sealed class FanStopModelContract
{
    private FanStopModelContract(string model, ExpectedHostIdentity host, ushort productId,
        string firmwareIdentity, Version ecVersion, string firmwareSha256,
        string contractDescription, bool physicallyValidated, ushort? boardSsid = null)
    {
        Model = model;
        Host = host;
        ProductId = productId;
        FirmwareIdentity = firmwareIdentity;
        EcVersion = ecVersion;
        FirmwareSha256 = firmwareSha256.ToUpperInvariant();
        ContractSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contractDescription)));
        PhysicallyValidated = physicallyValidated;
        BoardSsid = boardSsid;
    }

    internal static IReadOnlyList<FanStopModelContract> All { get; } = Array.AsReadOnly(new[]
    {
        new FanStopModelContract("RZ09-0581", WindowsHostIdentitySource.ProductionIdentity,
            0x02E0, "SO6120-EC1.04", new(1, 4),
            "8537e9fd55b7b35504d8d87b0191acc4391b67365b3fe72f6e1029a424c9e707",
            "SO6120 EC1.04 RZ09-0581 reviewed direct zero/Auto contract", true),
        // docs/fan-stop-so6110-source-review.md: independently reviewed EC1.09,
        // exact-model updater 300/301 ingress and selector-first Auto handback.
        // SMBIOS reports the EC version; it does not attest the running image hash.
        new FanStopModelContract("RZ09-0528", WindowsHostIdentitySource.Rz090528Identity,
            0x02C6, "SO6110-EC1.09", new(1, 9),
            "44dc4e4c92ea4be9def93670a95b2bf859cb648b8dfe393fb5877735debbef5d",
            "SO6110 EC1.09 RZ09-0528 reviewed direct zero/Auto contract", false),
        // Standard and Lamborghini use distinct firmware families. Their
        // read-only mapped E3/E4 board identifiers disambiguate version overlap.
        new FanStopModelContract("RZ09-0483", new("Razer", "Blade 16 - RZ09-0483", "", [],
                FirmwareAdmissionScope.ModelScoped),
            0x029F, "SO690-EC2.01", new(2, 1),
            "cbee52c89db0e9415865867ba053b415e0239bbec56a41ab25a35013370fca3c",
            "SO690 EC2.01 RZ09-0483 standard SSID3006 reviewed direct zero/Auto contract", false, 0x3006),
        new FanStopModelContract("RZ09-0483", new("Razer", "Blade 16 - RZ09-0483", "", [],
                FirmwareAdmissionScope.ModelScoped),
            0x029F, "SO695-LB-EC1.01", new(1, 1),
            "486daab302359085b421c0c68d8092be910930a2ef3912ec39dd8f04c89464b8",
            "SO695 EC1.01 RZ09-0483 Lamborghini SSID3009 reviewed direct zero/Auto contract", false, 0x3009),
        // docs/fan-stop-so6100-source-review.md: independent two-channel FE,
        // final command-clear and selector-first feedback review.
        new FanStopModelContract("RZ09-0510", new("Razer", "Blade 16 - RZ09-0510", "", [],
                FirmwareAdmissionScope.ModelScoped),
            0x02B7, "SO6100-EC1.02", new(1, 2),
            "d2ba85d82e76b594c448eaaa578df0ec2817af5af509d548a54a51a3845e4d43",
            "SO6100 EC1.02 RZ09-0510 reviewed direct zero/Auto contract", false),
        new FanStopModelContract("RZ09-0530", new("Razer", "Blade 14 - RZ09-0530", "", [],
                FirmwareAdmissionScope.ModelScoped),
            0x02C5, "PI4110-EC1.03", new(1, 3),
            "aecacca42102c17f4b2731a2ab28ebd92e154d0d997a708e41e7e36d56fc2224",
            "PI4110 EC1.03 RZ09-0530 reviewed direct zero/Auto contract", false),
        new FanStopModelContract("RZ09-0421", new("Razer", "Blade 15 (2022) - RZ09-0421", "", [],
                FirmwareAdmissionScope.ModelScoped),
            0x028A, "CH580-EC1.03", new(1, 3),
            "f96a009f65c49e89c76bb0874587472beace315a6cc616bc8da1bde845cb9c1b",
            "CH580 EC1.03 RZ09-0421 reviewed direct zero/Auto contract", false),
        new FanStopModelContract("RZ09-0427", new("Razer", "Blade 14 (2022) - RZ09-0427", "", [],
                FirmwareAdmissionScope.ModelScoped),
            0x028C, "PI480-EC1.03", new(1, 3),
            "312343a3cebd56fcb97cefcfc103b17bd481098e4122f2b6680ab46e93fc0b9f",
            "PI480 EC1.03 RZ09-0427 reviewed direct zero/Auto contract", false),
        new FanStopModelContract("RZ09-0427", new("Razer", "Blade 14 (2022) - RZ09-0427", "", [],
                FirmwareAdmissionScope.ModelScoped),
            0x028C, "PI480-EC1.06", new(1, 6),
            "2482e6ded196b6378e92ad857cc07b2f9db0b9aa392996172d09548f6cf6ef86",
            "PI480 EC1.06 RZ09-0427 reviewed direct zero/Auto contract", false),
        // The truncated DMI product name is the exact existing catalog identity.
        new FanStopModelContract("RZ09-0330", new("Razer", "Blade 15 Advanced Model (Early 2020) - RZ09-033", "", [],
                FirmwareAdmissionScope.ModelScoped),
            0x0253, "CH551-EC1.01", new(1, 1),
            "0487bd1357b6ac6c2c18b8e514b6a1098cb8e198b534e588045770a8c7140689",
            "CH551 EC1.01 RZ09-0330 reviewed direct zero/Auto contract", false),
    });

    internal string Model { get; }
    internal ExpectedHostIdentity Host { get; }
    internal ushort VendorId => 0x1532;
    internal ushort ProductId { get; }
    internal Version EcVersion { get; }
    internal string FirmwareIdentity { get; }
    internal string FirmwareSha256 { get; }
    internal string ContractSha256 { get; }
    internal bool PhysicallyValidated { get; }
    internal ushort? BoardSsid { get; }
    internal string DeviceIdentity => $"{Model}/{VendorId:X4}:{ProductId:X4}";
    internal HidHandleIdentity ControlHid => new(VendorId, ProductId, 0x0001, 0x0002, 91);

    internal static FanStopModelContract? ForModel(string model) =>
        All.FirstOrDefault(contract => contract.Model == model);

    internal static FanStopModelContract? ForFirmware(string model, Version? ecVersion) =>
        All.SingleOrDefault(contract => contract.Model == model && contract.EcVersion == ecVersion);

    internal static FanStopModelContract? ForCurrentWindowsHost()
    {
        var registry = new WindowsRegistryValueSource();
        var platform = new RuntimeWindowsPlatformEvidenceSource(registry);
        return All.FirstOrDefault(contract => contract.MatchesHost(
            new WindowsHostIdentitySource(registry, platform, contract.Host).ReadExactIdentity()));
    }

    internal bool MatchesHost(HostIdentityReadResult observed) =>
        observed.Failure is null && observed.Evidence is { } host
        && host.Manufacturer == Host.Manufacturer && host.ProductName == Host.ProductName;

    internal bool MatchesHid(HidHandleIdentity hid) => hid == ControlHid;

    internal bool IsTargetHidPath(string path)
    {
        string prefix = $@"\\?\hid#vid_{VendorId:x4}&pid_{ProductId:x4}";
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && path[prefix.Length] is '&' or '#';
    }
}
