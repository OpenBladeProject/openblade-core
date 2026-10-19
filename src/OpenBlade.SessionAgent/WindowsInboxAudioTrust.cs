using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

// Inbox audio packages have no local CAT. Establish membership in the current
// Windows servicing catalog; never pin a servicing build or catalog filename.
internal static class WindowsInboxAudioTrust
{
    internal static string VerifyFile(string path)
    {
        RazerKeyboardDriverPackage.EnsureNoReparsePoints(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Unexpected inbox audio member size.");
        }

        string identity = Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(file));
        file.Position = 0;
        Guid subsystem = new("F750E6C3-38EE-11D1-85E5-00C04FC295EE");
        if (!CryptCATAdminAcquireContext2(out IntPtr admin, ref subsystem, "SHA256", IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr catalog = IntPtr.Zero;
        try
        {
            uint length = 32;
            byte[] hash = new byte[length];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, file.SafeFileHandle, ref length, hash, 0) || length != hash.Length)
            {
                throw new CryptographicException("Windows could not hash the inbox audio member.");
            }

            for (int index = 0; index < 64; index++)
            {
                // Advancing the enumeration releases the previous context. The current
                // context is released in finally when returning before enumeration ends.
                catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, length, 0, ref catalog);
                if (catalog == IntPtr.Zero)
                {
                    break;
                }

                var info = new CatalogInfo { Size = (uint)Marshal.SizeOf<CatalogInfo>(), Path = string.Empty };
                if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                string catalogRoot = Path.Combine(Environment.SystemDirectory, "CatRoot") + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(info.Path).StartsWith(catalogRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                RazerKeyboardDriverPackage.EnsureNoReparsePoints(info.Path);
                using var catalogLock = File.OpenHandle(info.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    // The subject must belong to the signer of the successful trust
                    // provider state, not an unrelated certificate in the PKCS#7 bag.
                    ThxNativeComponentTrust.VerifyPublisher(info.Path, catalogLock,
                        "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
                        CancellationToken.None);
                    RazerDriverPackageTrust.VerifyCatalogMembers(Path.GetDirectoryName(info.Path)!, Path.GetFileName(info.Path),
                        [path], CancellationToken.None, cacheOnly: true);
                    return identity;
                }
                catch (CryptographicException) { /* Another current servicing catalog may be usable. */ }
            }
            throw new CryptographicException("No trusted Microsoft Windows servicing catalog contains this inbox audio member.");
        }
        finally
        {
            if (catalog != IntPtr.Zero)
            {
                CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
            }

            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo { internal uint Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Path; }
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, ref Guid subsystem, string algorithm, IntPtr policy, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file, ref uint length, [Out] byte[] hash, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint length, uint flags, ref IntPtr previous);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
