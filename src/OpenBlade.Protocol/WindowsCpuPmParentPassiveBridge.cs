namespace OpenBlade.Protocol;

internal sealed record CpuPmParentPassiveEvidence(bool ExactHostAndPackage, bool HealthyUniqueUsb,
    bool ControllersAbsent);

// Passive-only surface: it cannot create a mutex, device, service or query lease.
internal interface ICpuPmParentPassiveSource
{
    CpuPmParentPassiveEvidence ReadFresh();
}

internal sealed partial class WindowsRz090528CpuQueryAdapter
{
    internal static ICpuPmParentPassiveSource CreateParentPassiveSource() => new ParentPassiveSource();
    private sealed class ParentPassiveSource : ICpuPmParentPassiveSource
    {
        private readonly CpuQueryWindowsPlatform shared = new();
        private readonly PmNativePlatform pm;
        internal ParentPassiveSource() => pm = new(shared);
        public CpuPmParentPassiveEvidence ReadFresh()
        {
            var host = shared.ReadHost();
            var cpu = shared.ReadProcessor();
            var package = pm.ReadPackage();
            bool exact = host.Failure is null && host.Evidence is
            { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0528", Sku: "RZ09-05289EN4", BiosVersion: "2.02" }
                && cpu is { Vendor: "AuthenticAMD", Family: 0x1A, Model: 0x24, Stepping: 0 }
                && package.MaximumExtendedLeaf >= 0x80000001 && package.Package == 1;
            bool usb = ExactUsb(pm.ReadUsb());
            bool controllers = shared.ControllersAbsent();
            return new(exact, usb, controllers);
        }
    }
    internal static bool FilesExact(IReadOnlyList<CpuBackendFileMetadata> files) =>
        files.Count == Rz090528InstalledCpuBackendInspector.ExpectedHashes.Count
        && Rz090528InstalledCpuBackendInspector.ExpectedHashes.All(expected => files.Count(file =>
            string.Equals(file.Path, expected.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(file.Sha256, expected.Value, StringComparison.OrdinalIgnoreCase)
            && file.SignatureValid
            && string.Equals(file.SignerThumbprint, Rz090528InstalledCpuBackendInspector.SignerThumbprint,
                StringComparison.OrdinalIgnoreCase)) == 1);
}

internal sealed partial class WindowsCpuPmSessionContinuity
{
    // Exposes only callback/read operations. The parent reducer has no lifetime
    // parameter and cannot report ConfirmedFreshRunOwnedLifetime.
    internal static ICpuPmContinuityPlatform CreateParentPassivePlatform() => new NativePlatform();
}
