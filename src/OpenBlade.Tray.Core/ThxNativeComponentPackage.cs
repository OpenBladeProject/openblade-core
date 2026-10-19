using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Tray;

/// <summary>Acquires the reviewed user-mode client only; never installs drivers or invokes THX.</summary>
public static class ThxNativeComponentPackage
{
    public const string Version = "1.1.1.0";
    public const string FileName = "ThxV4Native_v1.1.1.0.dll";
    public const long Length = 13684872;
    public const string Sha256 = "8E8F6923624762935D70B72F58BEC00808DB84DF740340047919B53331130A0A";
    public const string SignerSubject = "CN=Razer USA Ltd., O=Razer USA Ltd., L=Irvine, S=California, C=US";
    public const string SignerThumbprint = "3FDDBD6F820144422494342EF44898D732E068DC";
    public static Uri DownloadUri { get; } = new("https://app-assets.razer.com/files/synapse/devices/common/THX/ThxV4Native_v1.1.1.0.dll");

    public static string GetInstalledPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenBlade", "Components", "THX", Version, FileName);

    public static string GetSynapsePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Razer", "RazerAppEngine", "User Data", "Apps", "Common", "thxv4common", FileName);

    public static bool HasVerifiedComponent() => TryResolve(out _);

    // File-only discovery: the exact byte pin identifies the reviewed signed artifact.
    // No network trust retrieval, native library loading, or audio operation occurs here.
    public static bool TryResolve(out string? path) =>
        TryResolve(GetInstalledPath(), GetSynapsePath(), out path);

    internal static bool TryResolve(string installed, string synapse, out string? path) =>
        TryResolve(installed, synapse, IsVerified, out path);

    internal static bool TryResolve(string installed, string synapse, Func<string, bool> verify, out string? path)
    {
        path = verify(installed) ? installed : verify(synapse) ? synapse : null;
        return path is not null;
    }

    private static bool IsVerified(string path)
    {
        try
        {
            using var file = OpenVerifiedRead(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Caller retains this read lock through native loading to prevent file replacement.</summary>
    public static FileStream OpenVerifiedRead(string path)
    {
        EnsureNoReparsePoints(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            EnsureNoReparsePoints(path);
            VerifyHash(file, Length, Sha256);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public static async Task<string> AcquireVerifiedAsync(CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        return await AcquireVerifiedAsync(GetInstalledPath(), client, ThxNativeComponentTrust.Verify,
            Length, Sha256, timeout.Token).ConfigureAwait(false);
    }

    internal static async Task<string> AcquireVerifiedAsync(string destination, HttpClient client,
        Action<string, SafeFileHandle, CancellationToken> verifyTrust,
        long expectedLength, string expectedHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        EnsureNoReparsePoints(destination);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(destination);
        string staging = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.download");
        bool published = false;
        try
        {
            // DELETE access allows an atomic rename through this same handle. No write/delete
            // sharing means the verified file cannot be substituted before publication.
            using SafeFileHandle handle = CreateFileW(staging, 0xC0010000, 1, nint.Zero, 1, 0x80, nint.Zero);
            if (handle.IsInvalid)
            {
                throw new IOException("Windows could not publish the verified THX component.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            using var output = new FileStream(handle, FileAccess.ReadWrite);
            await DownloadAsync(client, output, expectedLength, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Position = 0;
            VerifyHash(output, expectedLength, expectedHash);
            cancellationToken.ThrowIfCancellationRequested();
            verifyTrust(staging, handle, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(destination);
            Publish(handle, destination);
            published = true;
            return destination;
        }
        finally
        {
            if (!published)
            {
                // Only the unique file from this attempt is removed. A prior component is
                // untouched on download, cancellation, hash, trust, or publication failure.
                try { File.Delete(staging); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static async Task DownloadAsync(HttpClient client, Stream output, long expectedLength,
        CancellationToken cancellationToken)
    {
        Uri uri = DownloadUri;
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            ValidateDownloadUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("OpenBlade/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri location = response.Headers.Location ?? throw new InvalidDataException("The THX download redirect has no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != expectedLength)
            {
                throw new InvalidDataException("The THX component download size does not match the reviewed file.");
            }
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[16384];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > expectedLength)
                {
                    throw new InvalidDataException("The THX component download exceeded its reviewed size.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (total != expectedLength)
            {
                throw new InvalidDataException("The THX component download is incomplete.");
            }
            return;
        }
        throw new InvalidDataException("The THX component download redirected too many times.");
    }

    internal static void ValidateDownloadUri(Uri uri)
    {
        // The reviewed endpoint currently serves the artifact directly. Redirects may stay
        // on this Razer asset host; no other CDN or mutable catalog is implicitly trusted.
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.Host.Equals(DownloadUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The THX component download used an unapproved address.");
        }
    }

    private static void VerifyHash(Stream stream, long length, string hash)
    {
        if (stream.Length != length || !CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(hash), SHA256.HashData(stream)))
        {
            throw new InvalidDataException("The THX native component does not match the reviewed file.");
        }
    }

    internal static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("THX component paths cannot contain links or reparse points.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void Publish(SafeFileHandle handle, string destination)
    {
        int nameOffset = (int)Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.FileName));
        byte[] name = System.Text.Encoding.Unicode.GetBytes(destination + "\0");
        nint info = Marshal.AllocHGlobal(nameOffset + name.Length);
        try
        {
            Marshal.Copy(new byte[nameOffset], 0, info, nameOffset);
            Marshal.WriteByte(info, 1); // Replace an older file atomically only after verification.
            Marshal.WriteInt32(info, (int)Marshal.OffsetOf<FileRenameInfo>(nameof(FileRenameInfo.FileNameLength)), name.Length - sizeof(char));
            Marshal.Copy(name, 0, info + nameOffset, name.Length);
            if (!SetFileInformationByHandle(handle, 3, info, (uint)(nameOffset + name.Length)))
            {
                throw new IOException("Windows could not publish the verified THX component.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInfo
    {
        public byte ReplaceIfExists;
        public nint RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        nint securityAttributes, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, nint info, uint size);
}
