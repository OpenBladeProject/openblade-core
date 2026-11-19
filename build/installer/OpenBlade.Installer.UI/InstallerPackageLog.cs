using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Installer.UI;

internal static class InstallerPackageLog
{
    internal const int MaximumBytes = 256 * 1024;
    internal const int MaximumRecords = 512;
    internal const int MaximumRecordCharacters = 4096;

    internal static void AppendToBundleLog(
        string packageId, int status, string? path, Action<string> write)
    {
        if (packageId != OpenBladeBootstrapperApplication.OpenBladeSetupPackage)
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                write("OpenBlade.Setup package diagnostics unavailable: the package log path is missing.");
                return;
            }
            // Snapshot before calling Burn: its logging must never extend the file
            // being read, even if a log variable unexpectedly names the bundle log.
            using FileStream source = OpenRegularFile(path!);
            long length = source.Length;
            int count = (int)Math.Min(length, MaximumBytes);
            source.Seek(length - count, SeekOrigin.Begin);
            byte[] bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                int received = source.Read(bytes, read, count - read);
                if (received == 0) { break; }
                read += received;
            }

            Queue<string> records = new();
            bool truncated = length > count;
            int start = 0;
            if (truncated)
            {
                // Keep a partial first record: it may contain the final exception.
                // Only discard an incomplete UTF-8 prefix caused by the tail seek.
                while (start < read && start < 3 && (bytes[start] & 0xC0) == 0x80) { start++; }
            }
            else if (read >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                start = 3;
            }
            using StringReader reader = new(Encoding.UTF8.GetString(bytes, start, read - start));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                for (int offset = 0; offset < line.Length;)
                {
                    int size = Math.Min(MaximumRecordCharacters, line.Length - offset);
                    if (offset + size < line.Length && char.IsHighSurrogate(line[offset + size - 1])
                        && char.IsLowSurrogate(line[offset + size]))
                    {
                        size--;
                    }
                    if (records.Count == MaximumRecords)
                    {
                        records.Dequeue();
                        truncated = true;
                    }
                    records.Enqueue(SanitizeRecord(line.Substring(offset, size)));
                    offset += size;
                }
            }

            write($"OpenBlade.Setup package diagnostics begin (result: 0x{status:X8}).");
            if (truncated)
            {
                write("OpenBlade.Setup package diagnostics truncated: retained the bounded tail; its first record may be partial.");
            }
            foreach (string record in records)
            {
                write("OpenBlade.Setup package log: " + record);
            }
            if (records.Count == 0)
            {
                write("OpenBlade.Setup package log is empty.");
            }
            write("OpenBlade.Setup package diagnostics end.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or NotSupportedException or ArgumentException or Win32Exception)
        {
            TryWriteUnavailable(write, exception);
        }
        catch
        {
            // A diagnostic sink failure must not replace the package outcome.
        }
    }

    internal static string SanitizeRecord(string value)
    {
        char[] safe = value.ToCharArray();
        for (int index = 0; index < safe.Length; index++)
        {
            if (char.IsControl(safe[index]) || safe[index] is '\u2028' or '\u2029')
            {
                safe[index] = ' ';
            }
        }
        return new string(safe);
    }

    private static FileStream OpenRegularFile(string path)
    {
        string requested = Path.GetFullPath(path);
        SafeFileHandle handle = CreateFileW(requested, 0x80000000,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (GetFileType(handle) != 1)
            {
                throw new IOException("The package log is not a disk file.");
            }
            if (!GetFileInformationByHandle(handle, out FileInformation information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if ((information.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || information.NumberOfLinks != 1)
            {
                throw new IOException("The package log is not a regular file.");
            }
            StringBuilder finalPath = new(32768);
            uint size = GetFinalPathNameByHandleW(handle, finalPath, (uint)finalPath.Capacity, 0);
            if (size == 0) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            if (size >= finalPath.Capacity
                || !string.Equals(requested, NormalizeFinalPath(finalPath.ToString()), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The package log path was redirected.");
            }
            // FileStream takes ownership of this exact admitted handle; never reopen by path.
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path.Substring(8);
        }
        return path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path.Substring(4) : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess,
        FileShare shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,
        StringBuilder path, uint pathLength, uint flags);

    private static void TryWriteUnavailable(Action<string> write, Exception exception)
    {
        try
        {
            write($"OpenBlade.Setup package diagnostics unavailable: {exception.GetType().Name}.");
        }
        catch
        {
            // Do not retry or change installer execution for unavailable diagnostics.
        }
    }
}
