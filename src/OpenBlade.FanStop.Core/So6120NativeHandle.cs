using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.FanStop;

internal enum So6120NativeCloseState { NotAttempted, Closed, Failed }
internal sealed record So6120NativeCloseResult(bool Succeeded, int NativeError);
internal sealed class So6120NativeCloseEvidence
{
    private So6120NativeCloseResult? result;
    public So6120NativeCloseState State => Volatile.Read(ref result) switch
    {
        null => So6120NativeCloseState.NotAttempted,
        { Succeeded: true } => So6120NativeCloseState.Closed,
        _ => So6120NativeCloseState.Failed,
    };
    public int? NativeError => Volatile.Read(ref result)?.NativeError;
    internal void Record(So6120NativeCloseResult value) => Volatile.Write(ref result, value);
}

internal sealed class So6120OwnedInpOutHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Func<nint, So6120NativeCloseResult>? closeForTest;
    public So6120OwnedInpOutHandle() : base(ownsHandle: true) { }
    internal So6120OwnedInpOutHandle(nint value, Func<nint, So6120NativeCloseResult> close) : this()
    {
        closeForTest = close;
        SetHandle(value);
    }

    internal So6120NativeCloseEvidence Evidence { get; } = new();
    protected override bool ReleaseHandle()
    {
        So6120NativeCloseResult result;
        if (closeForTest is not null) { result = closeForTest(handle); }
        else
        {
            bool succeeded = CloseHandle(handle);
            result = new(succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
        }
        Evidence.Record(result);
        return result.Succeeded;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint value);
}
