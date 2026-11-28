using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Setup;

internal sealed record SetupCandidateProcessIdentity(uint ProcessId, long CreationFileTimeUtc, string ExecutablePath);

// Process evidence only. The image path identifies the process image name, not its loaded bytes.
internal sealed class WindowsSetupRetainedCandidateProcess : IDisposable
{
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint WaitTimeout = 258;
    private const int ImagePathCapacity = 32_768;
    private readonly object gate = new();
    private readonly SafeProcessHandle handle;
    private readonly SetupCandidateProcessIdentity expected;

    private WindowsSetupRetainedCandidateProcess(SafeProcessHandle handle, SetupCandidateProcessIdentity expected)
    {
        this.handle = handle;
        this.expected = expected;
    }

    // The fixture passes the original launch handle with query and SYNCHRONIZE access.
    // Duplicate it once without taking ownership of the caller's handle or reopening a PID.
    internal static WindowsSetupRetainedCandidateProcess? TryCapture(
        SafeProcessHandle original, SetupCandidateProcessIdentity expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || original.IsClosed || original.IsInvalid)
        {
            return null;
        }

        SafeProcessHandle duplicate;
        try
        {
            IntPtr current = GetCurrentProcess();
            if (!DuplicateHandle(current, original, current, out duplicate, 0, false, DuplicateSameAccess))
            {
                duplicate.Dispose();
                return null;
            }
        }
        catch (ObjectDisposedException)
        {
            // The caller closed its handle before interop could pin it for duplication.
            return null;
        }

        var retained = new WindowsSetupRetainedCandidateProcess(duplicate, expected);
        bool admitted = false;
        try
        {
            admitted = retained.ReadLive(cancellationToken) is not null;
            return admitted ? retained : null;
        }
        finally
        {
            if (!admitted)
            {
                retained.Dispose();
            }
        }
    }

    internal SetupCandidateProcessIdentity? ReadLive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handle.IsClosed || handle.IsInvalid)
            {
                return null;
            }

            uint processId = GetProcessId(handle);
            if (processId == 0 || processId != expected.ProcessId
                || !GetProcessTimes(handle, out FILETIME creation, out _, out _, out _))
            {
                return null;
            }

            long creationFileTime = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
            if (creationFileTime <= 0 || creationFileTime != expected.CreationFileTimeUtc)
            {
                return null;
            }

            var path = new StringBuilder(ImagePathCapacity);
            int length = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length))
            {
                return null;
            }

            string imagePath = path.ToString();
            if (!Path.IsPathFullyQualified(imagePath)
                || !string.Equals(imagePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Check last: a signaled handle means exit even if the process returned STILL_ACTIVE (259).
            bool running = WaitForSingleObject(handle, 0) == WaitTimeout;
            cancellationToken.ThrowIfCancellationRequested();
            return running ? new SetupCandidateProcessIdentity(processId, creationFileTime, imagePath) : null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            handle.Dispose();
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess, SafeProcessHandle sourceHandle, IntPtr targetProcess,
        out SafeProcessHandle targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, StringBuilder path, ref int length);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
