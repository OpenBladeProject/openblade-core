using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

public sealed record StagedVerifiedLocalDevDeployment(
    OpenBladeVerifiedRelease VerifiedRelease,
    byte[] ManifestBytes,
    byte[] SignatureBytes,
    string InstallerPath);

public interface IFixedLocalDevInboxSource
{
    Task<StagedVerifiedLocalDevDeployment> StageAsync(
        string protectedStagingDirectory,
        OpenBladeReleaseTrustPolicy trustPolicy,
        CancellationToken cancellationToken = default);
}

public sealed class FixedLocalDevInboxSource(BrokerPaths paths) : IFixedLocalDevInboxSource
{
    public const string ManifestFileName = "OpenBlade-Local-Dev-Deployment.json";
    public const string SignatureFileName = "OpenBlade-Local-Dev-Deployment.sig";
    public const string InstallerFileName = "OpenBlade-Setup.exe";
    public const int SignatureBytes = OpenBladeSignedReleaseVerifier.P1363SignatureBytes;

    private static readonly string[] ExactFileNames =
    [
        ManifestFileName,
        SignatureFileName,
        InstallerFileName,
    ];

    public async Task<StagedVerifiedLocalDevDeployment> StageAsync(
        string protectedStagingDirectory,
        OpenBladeReleaseTrustPolicy trustPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedStagingDirectory);
        ArgumentNullException.ThrowIfNull(trustPolicy);
        string inbox = Path.GetFullPath(paths.InboxDirectory);
        string staging = Path.GetFullPath(protectedStagingDirectory);
        ProtectedBrokerStorage.RejectReparsePoint(inbox);
        ProtectedBrokerStorage.RejectReparsePoint(staging);
        RequireExactInboxSet(inbox);

        await using FileStream manifest = OpenExclusiveRegularFile(inbox, ManifestFileName);
        await using FileStream signature = OpenExclusiveRegularFile(inbox, SignatureFileName);
        await using FileStream installer = OpenExclusiveRegularFile(inbox, InstallerFileName);
        RequireExactInboxSet(inbox);

        byte[] manifestBytes = await ReadBoundedAsync(
            manifest,
            OpenBladeReleaseManifestCodec.MaximumManifestBytes,
            cancellationToken).ConfigureAwait(false);
        byte[] signatureBytes = await ReadBoundedAsync(
            signature,
            SignatureBytes,
            cancellationToken).ConfigureAwait(false);
        if (signatureBytes.Length != SignatureBytes)
        {
            throw new InvalidDataException(
                $"The local development deployment signature must contain exactly {SignatureBytes} bytes.");
        }

        await WriteProtectedMetadataAsync(
            Path.Combine(staging, ManifestFileName),
            manifestBytes,
            cancellationToken).ConfigureAwait(false);
        await WriteProtectedMetadataAsync(
            Path.Combine(staging, SignatureFileName),
            signatureBytes,
            cancellationToken).ConfigureAwait(false);

        OpenBladeVerifiedRelease verified = OpenBladeSignedReleaseVerifier.Verify(
            manifestBytes,
            signatureBytes,
            trustPolicy);
        if (!string.Equals(
                verified.Manifest.InstallerFileName,
                InstallerFileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The signed local development installer filename is invalid.");
        }

        string destination = Path.Combine(staging, InstallerFileName);
        await CopyInstallerAsync(
            installer,
            destination,
            verified.Manifest.InstallerSize,
            cancellationToken).ConfigureAwait(false);
        paths.ProtectFile(destination);
        paths.VerifyFile(destination);

        await using (FileStream staged = new(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await OpenBladeSignedReleaseVerifier.VerifyInstallerAsync(
                staged,
                verified,
                cancellationToken).ConfigureAwait(false);
        }

        return new StagedVerifiedLocalDevDeployment(
            verified,
            manifestBytes,
            signatureBytes,
            destination);
    }

    private static FileStream OpenExclusiveRegularFile(string inbox, string exactName)
    {
        string path = Path.Combine(inbox, exactName);
        ProtectedBrokerStorage.RejectReparsePoint(path);
        FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out ByHandleFileInformation info))
            {
                throw new IOException(
                    "The local development inbox file identity is unavailable.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            if ((info.FileAttributes & (uint)FileAttributes.Directory) != 0
                || (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0
                || info.NumberOfLinks != 1)
            {
                throw new UnauthorizedAccessException(
                    "The local development inbox accepts only single-link regular files.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void RequireExactInboxSet(string inbox)
    {
        string[] entries = Directory.EnumerateFileSystemEntries(inbox)
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = ExactFileNames.Order(StringComparer.Ordinal).ToArray();
        if (!entries.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The local development inbox must contain exactly the three fixed deployment files.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[Math.Min(81920, maximumBytes + 1)];
        while (output.Length <= maximumBytes)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new InvalidDataException("A local development inbox metadata file exceeds its fixed bound.");
    }

    private static async Task CopyInstallerAsync(
        Stream source,
        string destination,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength <= 0 || expectedLength > OpenBladeReleaseManifestCodec.MaximumInstallerBytes)
        {
            throw new InvalidDataException("The signed local development installer size is invalid.");
        }

        await using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        byte[] buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            copied += read;
            if (copied > expectedLength)
            {
                throw new InvalidDataException("The local development installer exceeds its signed size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        if (copied != expectedLength)
        {
            throw new InvalidDataException("The local development installer size differs from its manifest.");
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private async Task WriteProtectedMetadataAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using (FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        paths.ProtectFile(destination);
        paths.VerifyFile(destination);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);
}
