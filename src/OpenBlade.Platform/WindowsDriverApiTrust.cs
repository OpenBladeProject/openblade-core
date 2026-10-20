using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Platform;

/// <summary>Offline Windows trust checks for installed driver API files.</summary>
public static class WindowsDriverApiTrust
{
    public const string MicrosoftHardwareCompatibilityPublisher =
        "CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    internal const int MaximumMemberCount = 128;
    internal const int MaximumPathLength = 4096;
    internal const long MaximumFileLength = 256L * 1024 * 1024;

    private const long MaximumTotalLength = 1024L * 1024 * 1024;
    private const uint TrustUiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint ChoiceCatalog = 2;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckChainExcludeRoot = 0x80;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const uint DisableMd2Md4 = 0x2000;

    private static readonly Guid GenericVerify = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private static readonly Guid DriverVerify = new("F750E6C3-38EE-11D1-85E5-00C04FC295EE");

    public static string VerifyCatalogMembers(string catalogPath, IReadOnlyList<string> memberPaths,
        IReadOnlyList<string> expectedPublishers, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(memberPaths);
        if (memberPaths.Count == 0 || memberPaths.Count > MaximumMemberCount)
        {
            throw new InvalidDataException($"A driver catalog must have between 1 and {MaximumMemberCount} members.");
        }

        string normalizedCatalog = NormalizeFilePath(catalogPath);
        string[] normalizedMembers = new string[memberPaths.Count];
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedCatalog };
        for (int index = 0; index < memberPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedMember = NormalizeFilePath(memberPaths[index]);
            if (!uniquePaths.Add(normalizedMember))
            {
                throw new InvalidDataException("A catalog or member path was supplied more than once.");
            }
            normalizedMembers[index] = normalizedMember;
        }

        string[] publishers = ValidatePublishers(expectedPublishers);
        var files = new List<LockedFile>(normalizedMembers.Length + 1);
        try
        {
            files.Add(OpenLockedFile(normalizedCatalog));
            foreach (string member in normalizedMembers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(OpenLockedFile(member));
            }
            EnsureTotalLength(files);

            VerifyFileTrust(files[0], GenericVerify, publishers, "driver catalog", cancellationToken);
            for (int index = 1; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifyCatalogMember(files[0].Path, files[index], cancellationToken);
            }

            return ComputeObservedContentIdentity(files, cancellationToken);
        }
        finally
        {
            for (int index = files.Count - 1; index >= 0; index--)
            {
                files[index].Dispose();
            }
        }
    }

    public static string VerifyAuthenticodeFile(string path, IReadOnlyList<string> expectedPublishers,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedPath = NormalizeFilePath(path);
        string[] publishers = ValidatePublishers(expectedPublishers);
        using LockedFile file = OpenLockedFile(normalizedPath);
        VerifyFileTrust(file, GenericVerify, publishers, "driver API file", cancellationToken);
        return ComputeObservedContentIdentity(new[] { file }, cancellationToken);
    }

    internal static bool MatchesExpectedPublisher(string actualSubject, IReadOnlyList<string> expectedPublishers)
    {
        ArgumentNullException.ThrowIfNull(actualSubject);
        ArgumentNullException.ThrowIfNull(expectedPublishers);
        return expectedPublishers.Any(expected => string.Equals(actualSubject, expected, StringComparison.Ordinal));
    }

    internal static SafeFileHandle OpenLockedFileForTest(string path) => OpenLockedFile(NormalizeFilePath(path)).DetachHandle();

    internal static void EnsureNoReparsePoints(string path, Func<string, FileAttributes> getAttributes)
    {
        ArgumentNullException.ThrowIfNull(getAttributes);
        string? current = path;
        string root = Path.GetPathRoot(path)!;
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((getAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Driver API paths cannot contain reparse points.");
            }
            current = Path.GetDirectoryName(current);
            if (current is null)
            {
                throw new InvalidDataException("The driver API path has no local filesystem root.");
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows is required to verify driver API trust.");
        }
    }

    private static string NormalizeFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > MaximumPathLength || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Driver API paths must be bounded, fully qualified local paths.");
        }

        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        if (fullPath.Length > MaximumPathLength || root is null || root.Length != 3 || root[1] != ':' ||
            fullPath.AsSpan(root.Length).Contains(':'))
        {
            throw new InvalidDataException("Driver API paths must use a bounded local drive path without alternate streams.");
        }
        return fullPath;
    }

    private static string[] ValidatePublishers(IReadOnlyList<string> expectedPublishers)
    {
        ArgumentNullException.ThrowIfNull(expectedPublishers);
        if (expectedPublishers.Count == 0 || expectedPublishers.Count > 16)
        {
            throw new InvalidDataException("At least one bounded publisher subject is required.");
        }

        var publishers = new string[expectedPublishers.Count];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < expectedPublishers.Count; index++)
        {
            string publisher = expectedPublishers[index] ?? string.Empty;
            if (publisher.Length == 0 || publisher.Length > 512 || publisher != publisher.Trim() || !unique.Add(publisher))
            {
                throw new InvalidDataException("Publisher subjects must be non-empty, bounded, unique full subjects.");
            }
            publishers[index] = publisher;
        }
        return publishers;
    }

    private static LockedFile OpenLockedFile(string path)
    {
        EnsureNoReparsePoints(path, File.GetAttributes);
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.SequentialScan);
        try
        {
            EnsureNoReparsePoints(path, File.GetAttributes);
            long length = RandomAccess.GetLength(handle);
            if (length <= 0 || length > MaximumFileLength)
            {
                throw new InvalidDataException($"Driver API files must contain between 1 and {MaximumFileLength} bytes.");
            }
            return new LockedFile(path, handle, length);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void EnsureTotalLength(IReadOnlyList<LockedFile> files)
    {
        long total = 0;
        foreach (LockedFile file in files)
        {
            total = checked(total + file.Length);
            if (total > MaximumTotalLength)
            {
                throw new InvalidDataException("The driver catalog and member files exceed the total size bound.");
            }
        }
    }

    private static void VerifyFileTrust(LockedFile file, Guid action, IReadOnlyList<string> expectedPublishers,
        string description, CancellationToken cancellationToken)
    {
        var info = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            Path = file.Path,
            Handle = file.Handle.DangerousGetHandle(),
        };
        VerifyObject(info, ChoiceFile, action, expectedPublishers, description, cancellationToken);
    }

    private static void VerifyCatalogMember(string catalogPath, LockedFile member,
        CancellationToken cancellationToken)
    {
        Guid subsystem = DriverVerify;
        if (!CryptCATAdminAcquireContext2(out nint admin, ref subsystem, "SHA256", nint.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint hashLength = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, member.Handle, ref hashLength, null, 0) ||
                hashLength == 0 || hashLength > 128)
            {
                throw new CryptographicException("Windows could not size the driver catalog member hash.");
            }

            var hash = new byte[hashLength];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, member.Handle, ref hashLength, hash, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            GCHandle hashPin = GCHandle.Alloc(hash, GCHandleType.Pinned);
            try
            {
                var info = new WinTrustCatalogInfo
                {
                    Size = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(),
                    CatalogPath = catalogPath,
                    MemberTag = Convert.ToHexString(hash),
                    MemberPath = member.Path,
                    MemberHandle = member.Handle.DangerousGetHandle(),
                    Hash = hashPin.AddrOfPinnedObject(),
                    HashLength = hashLength,
                    CatalogAdmin = admin,
                };
                VerifyObject(info, ChoiceCatalog, DriverVerify, null, "driver catalog member", cancellationToken);
            }
            finally
            {
                hashPin.Free();
            }
        }
        finally
        {
            _ = CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static void VerifyObject<T>(T info, uint choice, Guid action,
        IReadOnlyList<string>? expectedPublishers, string description,
        CancellationToken cancellationToken) where T : struct
    {
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = TrustUiNone,
                RevocationChecks = RevokeWholeChain,
                UnionChoice = choice,
                Info = pointer,
                StateAction = StateActionVerify,
                ProviderFlags = RevocationCheckChainExcludeRoot | CacheOnlyUrlRetrieval | DisableMd2Md4,
            };
            try
            {
                int status = WinVerifyTrust(new nint(-1), ref action, ref data);
                if (status != 0)
                {
                    throw new CryptographicException($"Windows rejected {description} trust (0x{status:X8}).");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (expectedPublishers is not null)
                {
                    string publisher = GetVerifiedPublisher(data.State, description);
                    if (!MatchesExpectedPublisher(publisher, expectedPublishers))
                    {
                        throw new CryptographicException($"The verified {description} publisher is not allowed.");
                    }
                }
            }
            finally
            {
                data.StateAction = StateActionClose;
                _ = WinVerifyTrust(new nint(-1), ref action, ref data);
            }
        }
        finally
        {
            Marshal.DestroyStructure<T>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static string GetVerifiedPublisher(nint state, string description)
    {
        nint provider = WTHelperProvDataFromStateData(state);
        nint signer = provider == nint.Zero ? nint.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        nint certificate = signer == nint.Zero ? nint.Zero : WTHelperGetProvCertFromChain(signer, 0);
        if (certificate == nint.Zero)
        {
            throw new CryptographicException($"Windows did not provide the verified {description} signer.");
        }

        ProviderCertificateHeader header = Marshal.PtrToStructure<ProviderCertificateHeader>(certificate);
        if (header.Certificate == nint.Zero)
        {
            throw new CryptographicException($"Windows did not provide the verified {description} certificate.");
        }
#pragma warning disable SYSLIB0057
        using var signedBy = new X509Certificate2(header.Certificate);
#pragma warning restore SYSLIB0057
        return signedBy.Subject;
    }

    private static string ComputeObservedContentIdentity(IReadOnlyList<LockedFile> files,
        CancellationToken cancellationToken)
    {
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        identity.AppendData("OpenBlade.WindowsDriverApiTrust.v1\0"u8);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(number, files.Count);
        identity.AppendData(number[..sizeof(int)]);

        foreach (LockedFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(file.Path).ToUpperInvariant());
            BinaryPrimitives.WriteInt32LittleEndian(number, name.Length);
            identity.AppendData(number[..sizeof(int)]);
            identity.AppendData(name);
            BinaryPrimitives.WriteInt64LittleEndian(number, file.Length);
            identity.AppendData(number);
            identity.AppendData(HashFile(file, cancellationToken));
        }
        return "SHA256:" + Convert.ToHexString(identity.GetHashAndReset());
    }

    private static byte[] HashFile(LockedFile file, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while (offset < file.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = RandomAccess.Read(file.Handle, buffer.AsSpan(0,
                    (int)Math.Min(buffer.Length, file.Length - offset)), offset);
                if (read == 0)
                {
                    throw new EndOfStreamException("A locked driver API file changed while its identity was read.");
                }
                hash.AppendData(buffer.AsSpan(0, read));
                offset += read;
            }
            if (RandomAccess.GetLength(file.Handle) != file.Length)
            {
                throw new IOException("A locked driver API file changed during verification.");
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed class LockedFile : IDisposable
    {
        private SafeFileHandle? handle;

        internal LockedFile(string path, SafeFileHandle handle, long length)
        {
            Path = path;
            this.handle = handle;
            Length = length;
        }

        internal string Path { get; }
        internal SafeFileHandle Handle => handle ?? throw new ObjectDisposedException(nameof(LockedFile));
        internal long Length { get; }

        internal SafeFileHandle DetachHandle()
        {
            SafeFileHandle result = Handle;
            handle = null;
            return result;
        }

        public void Dispose()
        {
            handle?.Dispose();
            handle = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificateHeader
    {
        public uint Size;
        public nint Certificate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public nint Handle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustCatalogInfo
    {
        public uint Size;
        public uint Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string CatalogPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberPath;
        public nint MemberHandle;
        public nint Hash;
        public uint HashLength;
        public nint CatalogContext;
        public nint CatalogAdmin;
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

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out nint admin, ref Guid subsystem,
        string algorithm, nint policy, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(nint admin, SafeFileHandle file,
        ref uint hashLength, [Out] byte[]? hash, uint flags);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(nint admin, uint flags);
}
