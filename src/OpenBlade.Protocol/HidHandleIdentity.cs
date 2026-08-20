using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Protocol;

internal readonly record struct HidOpenParameters(
    uint DesiredAccess,
    uint ShareMode,
    uint CreationDisposition,
    uint FlagsAndAttributes,
    bool Inheritable);

internal readonly record struct HidHandleIdentity(
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort FeatureReportByteLength,
    ushort VersionNumber = 0)
{
    internal bool MatchesExpected(HidHandleIdentity expected) =>
        VendorId == expected.VendorId
        && ProductId == expected.ProductId
        && UsagePage == expected.UsagePage
        && Usage == expected.Usage
        && FeatureReportByteLength == expected.FeatureReportByteLength
        && VersionNumber == expected.VersionNumber;
}

internal readonly record struct HidCandidateOpenResult(
    OpenedCandidateHandle? Candidate,
    int? Win32Error);

internal sealed class OpenedCandidateHandle : IDisposable
{
    private readonly Action disposeFileHandle;
    private int ownsFileHandle = 1;

    public OpenedCandidateHandle(SafeFileHandle fileHandle)
        : this(fileHandle, fileHandle.Dispose)
    {
    }

    internal OpenedCandidateHandle(SafeFileHandle fileHandle, Action disposeFileHandle)
    {
        ArgumentNullException.ThrowIfNull(fileHandle);
        ArgumentNullException.ThrowIfNull(disposeFileHandle);
        FileHandle = fileHandle;
        this.disposeFileHandle = disposeFileHandle;
    }

    public SafeFileHandle FileHandle { get; }

    internal SafeFileHandle TransferOwnership()
    {
        if (Interlocked.Exchange(ref ownsFileHandle, 0) == 0)
        {
            throw new ObjectDisposedException(nameof(OpenedCandidateHandle));
        }

        return FileHandle;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref ownsFileHandle, 0) != 0)
        {
            disposeFileHandle();
        }
    }
}

internal sealed class OpenedHidHandle : IAsyncDisposable
{
    private readonly Action disposeBoundHandle;
    private readonly Action disposeFileHandle;
    private int disposed;

    public OpenedHidHandle(SafeFileHandle fileHandle, ThreadPoolBoundHandle boundHandle)
        : this(fileHandle, boundHandle, boundHandle.Dispose, fileHandle.Dispose)
    {
    }

    internal OpenedHidHandle(
        SafeFileHandle fileHandle,
        ThreadPoolBoundHandle boundHandle,
        Action disposeBoundHandle,
        Action disposeFileHandle)
    {
        ArgumentNullException.ThrowIfNull(fileHandle);
        ArgumentNullException.ThrowIfNull(boundHandle);
        ArgumentNullException.ThrowIfNull(disposeBoundHandle);
        ArgumentNullException.ThrowIfNull(disposeFileHandle);
        FileHandle = fileHandle;
        BoundHandle = boundHandle;
        this.disposeBoundHandle = disposeBoundHandle;
        this.disposeFileHandle = disposeFileHandle;
    }

    public SafeFileHandle FileHandle { get; }

    public ThreadPoolBoundHandle BoundHandle { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try
            {
                disposeBoundHandle();
            }
            finally
            {
                disposeFileHandle();
            }
        }

        return ValueTask.CompletedTask;
    }
}

internal interface IHidCandidateNative
{
    IReadOnlyList<string> EnumerateDevicePaths();

    HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters);

    HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle);

    OpenedHidHandle BindSelected(OpenedCandidateHandle handle);
}
