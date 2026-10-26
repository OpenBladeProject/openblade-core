using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Tray;

/// <summary>Authenticode signer policy from successful Windows trust-provider state.</summary>
internal static class ThxNativeComponentTrust
{
    internal static void Verify(string path, SafeFileHandle handle, CancellationToken cancellationToken)
        => Verify(path, handle, ThxNativeComponentPackage.SignerSubject,
            ThxNativeComponentPackage.SignerThumbprint, cancellationToken);

    internal static void Verify(string path, SafeFileHandle handle, string signerSubject,
        string signerThumbprint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Existing native-client callers must always provide both reviewed pins.
        // They cannot opt into the separate Windows publisher-only policy.
        if (string.IsNullOrEmpty(signerThumbprint))
        {
            throw new CryptographicException("A pinned component requires its reviewed signer thumbprint.");
        }
        VerifyCore(path, handle, signerSubject, signerThumbprint, cacheOnly: false, cancellationToken);
    }

    // Windows servicing certificates rotate. Require the verified publisher, while
    // leaving current Windows chain, timestamp and cached revocation policy intact.
    internal static void VerifyPublisher(string path, SafeFileHandle handle, string signerSubject,
        CancellationToken cancellationToken) =>
        VerifyCore(path, handle, signerSubject, signerThumbprint: null, cacheOnly: true, cancellationToken);

    private static void VerifyCore(string path, SafeFileHandle handle, string signerSubject,
        string? signerThumbprint, bool cacheOnly, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(signerSubject))
        {
            throw new CryptographicException("A verified signer subject is required.");
        }
        var file = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            Path = path,
            Handle = handle.DangerousGetHandle(),
        };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 1,
                UnionChoice = 1,
                Info = pointer,
                StateAction = 1,
                ProviderFlags = 0x80 | 0x2000 | (cacheOnly ? 0x1000u : 0u), // Chain excluding root; reject MD2/MD4. Honor timestamps.
            };
            try
            {
                int status = WinVerifyTrust(new nint(-1), ref action, ref data);
                if (status != 0)
                {
                    throw new CryptographicException($"Windows rejected signed component trust (0x{status:X8}).");
                }
                cancellationToken.ThrowIfCancellationRequested();
                VerifySigner(data.State, signerSubject, signerThumbprint);
            }
            finally
            {
                data.StateAction = 2;
                _ = WinVerifyTrust(new nint(-1), ref action, ref data);
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void VerifySigner(nint state, string signerSubject, string? signerThumbprint)
    {
        // Read the signer from the successful trust provider state. Reopening the path
        // would conflict with the retained DELETE-access handle used for atomic publish.
        nint provider = WTHelperProvDataFromStateData(state);
        nint signer = provider == nint.Zero ? nint.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        nint certificate = signer == nint.Zero ? nint.Zero : WTHelperGetProvCertFromChain(signer, 0);
        if (certificate == nint.Zero)
        {
            throw new CryptographicException("Windows did not provide the verified signed component signer.");
        }
        var info = Marshal.PtrToStructure<ProviderCertificateHeader>(certificate);
        if (info.Certificate == nint.Zero)
        {
            throw new CryptographicException("Windows did not provide the verified signed component certificate.");
        }
#pragma warning disable SYSLIB0057
        using var signedBy = new X509Certificate2(info.Certificate);
#pragma warning restore SYSLIB0057
        if (!MatchesSigner(signedBy.Subject, signedBy.Thumbprint, signerSubject, signerThumbprint))
        {
            throw new CryptographicException("The verified signer does not match the required publisher and signer pins.");
        }
    }

    internal static bool MatchesSigner(string actualSubject, string actualThumbprint,
        string expectedSubject, string? expectedThumbprint) =>
        !string.IsNullOrEmpty(expectedSubject)
        && string.Equals(actualSubject, expectedSubject, StringComparison.Ordinal)
        && (expectedThumbprint is null || !string.IsNullOrEmpty(expectedThumbprint)
            && string.Equals(actualThumbprint, expectedThumbprint, StringComparison.Ordinal));

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificateHeader
    {
        public uint Size;
        public nint Certificate;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public nint Handle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallback;
        public nint SipClient;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint Info;
        public uint StateAction;
        public nint State;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);
}
