using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Tray;

/// <summary>Read-only Windows trust checks for the pinned catalogs and their INF/SYS members.</summary>
internal static class RazerDriverPackageTrust
{
    private static readonly Guid GenericVerify = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private static readonly Guid DriverVerify = new("F750E6C3-38EE-11D1-85E5-00C04FC295EE");
    private const string Publisher = "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    public static void Verify(string packageRoot, CancellationToken cancellationToken) =>
        Verify(packageRoot, cancellationToken, cacheOnly: false);

    internal static void Verify(string packageRoot, CancellationToken cancellationToken, bool cacheOnly)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows is required to verify the driver catalogs.");
        }

        foreach (var catalog in RazerKeyboardDriverPackage.Files.Where(file => file.RelativePath.EndsWith(".cat", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var catalogPath = Path.Combine(packageRoot, catalog.RelativePath);
            RazerKeyboardDriverPackage.EnsureNoReparsePoints(catalogPath);
            using var catalogHandle = File.OpenHandle(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyCatalog(catalogPath, catalogHandle, cacheOnly);
            var directory = Path.GetDirectoryName(catalog.RelativePath);
            foreach (var member in RazerKeyboardDriverPackage.Files.Where(file =>
                Path.GetDirectoryName(file.RelativePath) == directory && file != catalog))
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifyMember(catalogPath, Path.Combine(packageRoot, member.RelativePath), cacheOnly);
            }
        }
    }

    internal static void VerifyCatalogMembers(string packageRoot, string catalogName,
        IEnumerable<string> memberNames, CancellationToken cancellationToken, bool cacheOnly)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows is required to verify the driver catalog.");
        }

        string catalogPath = Path.Combine(packageRoot, catalogName);
        RazerKeyboardDriverPackage.EnsureNoReparsePoints(catalogPath);
        using var catalogHandle = File.OpenHandle(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        VerifyCatalogTrust(catalogPath, catalogHandle, cacheOnly);
        foreach (string memberName in memberNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyMember(catalogPath, Path.Combine(packageRoot, memberName), cacheOnly);
        }
    }

    private static void VerifyCatalog(string path, SafeFileHandle handle, bool cacheOnly)
    {
        VerifyCatalogTrust(path, handle, cacheOnly);
        // Windows validates the signature and timestamp above; these pins also enforce the
        // exact Hardware Compatibility Publisher identity recorded by the reviewed manifest.
#pragma warning disable SYSLIB0057
        using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        var expectedThumbprint = Path.GetFileName(path) == "rzcommon.cat"
            ? "FAC666005546D6BE881A31C1267717879401A950"
            : "32B4E7CE92A59C722B8BDF7AE3E66520CFB9DE21";
        if (certificate.Subject != Publisher || certificate.GetCertHashString() != expectedThumbprint)
        {
            throw new CryptographicException("The driver catalog publisher does not match the reviewed Microsoft signature.");
        }
    }

    private static void VerifyCatalogTrust(string path, SafeFileHandle handle, bool cacheOnly)
    {
        var fileInfo = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            Path = path,
            Handle = handle.DangerousGetHandle(),
        };
        VerifyObject(fileInfo, 1, GenericVerify, cacheOnly);
    }

    private static void VerifyMember(string catalogPath, string memberPath, bool cacheOnly)
    {
        RazerKeyboardDriverPackage.EnsureNoReparsePoints(memberPath);
        using var file = File.OpenHandle(memberPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var subsystem = DriverVerify;
        if (!CryptCATAdminAcquireContext2(out var admin, ref subsystem, "SHA256", IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            uint length = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref length, null, 0) || length != 32)
            {
                throw new CryptographicException("Windows could not calculate the driver catalog member hash.");
            }

            var hash = new byte[length];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref length, hash, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var hashPin = GCHandle.Alloc(hash, GCHandleType.Pinned);
            try
            {
                var info = new WinTrustCatalogInfo
                {
                    Size = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(),
                    CatalogPath = catalogPath,
                    MemberTag = Convert.ToHexString(hash),
                    MemberPath = memberPath,
                    MemberHandle = file.DangerousGetHandle(),
                    Hash = hashPin.AddrOfPinnedObject(),
                    HashLength = length,
                    CatalogAdmin = admin,
                };
                // WTD_CHOICE_CATALOG verifies actual membership; embedded SYS/INF signatures
                // are neither required nor substituted for the supplied catalog.
                VerifyObject(info, 2, DriverVerify, cacheOnly);
            }
            finally
            {
                hashPin.Free();
            }
        }
        finally
        {
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static void VerifyObject<T>(T info, uint choice, Guid action, bool cacheOnly) where T : struct
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 1, // Whole chain; unavailable revocation evidence fails closed.
                UnionChoice = choice,
                Info = pointer,
                StateAction = 1,
                ProviderFlags = (cacheOnly ? 0x1000u : 0u) | 0x80u | 0x2000u, // Exclude root, reject MD2/MD4.
            };
            try
            {
                var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                if (status != 0)
                {
                    throw new CryptographicException($"Windows rejected driver package trust (0x{status:X8}).");
                }
            }
            finally
            {
                data.StateAction = 2;
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
        finally
        {
            Marshal.DestroyStructure<T>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustCatalogInfo
    {
        public uint Size;
        public uint Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string CatalogPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberPath;
        public IntPtr MemberHandle;
        public IntPtr Hash;
        public uint HashLength;
        public IntPtr CatalogContext;
        public IntPtr CatalogAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallback;
        public IntPtr SipClient;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr Info;
        public uint StateAction;
        public IntPtr State;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, ref Guid subsystem,
        string algorithm, IntPtr policy, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file,
        ref uint hashLength, [Out] byte[]? hash, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
