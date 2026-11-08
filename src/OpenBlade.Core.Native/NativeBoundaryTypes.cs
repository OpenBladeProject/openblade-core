using System.Runtime.InteropServices;

namespace OpenBlade.Core.Native;

public enum NativeBoundaryErrorKind
{
    AbiMismatch = 1,
    InvalidArgument = 2,
    BufferTooSmall = 3,
    ProtocolValidation = 4,
    InternalFailure = 5,
    UnsupportedPlatform = 100,
    InvalidLibraryPath = 101,
    LibraryLoadFailure = 102,
    MissingExport = 103,
    InvalidLayout = 104,
    InvalidNativeResult = 105,
    Disposed = 106,
}

public sealed record NativeBoundaryError(
    NativeBoundaryErrorKind Kind, uint Detail, uint RequiredLength, string Message);

public sealed record NativeCodecResult(byte[]? Output, NativeBoundaryError? Error)
{
    public bool IsSuccess => Output is not null && Error is null;
}

public sealed record NativeCodecLoadResult(NativeReportCodec? Codec, NativeBoundaryError? Error)
{
    public bool IsSuccess => Codec is not null && Error is null;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeReportRequest
{
    internal uint StructSize;
    internal byte Transaction;
    internal byte CommandClass;
    internal byte CommandId;
    internal byte Reserved;

    internal static NativeReportRequest Create(byte transaction, byte commandClass, byte commandId)
        => new() { StructSize = 8, Transaction = transaction, CommandClass = commandClass, CommandId = commandId };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeValidationOptions
{
    internal uint StructSize;
    internal int ExpectedLength;
    internal byte HasExpected;
    internal byte Reserved0;
    internal byte Reserved1;
    internal byte Reserved2;

    internal static NativeValidationOptions Create(int? expectedLength)
        => new() { StructSize = 12, ExpectedLength = expectedLength ?? 0, HasExpected = expectedLength.HasValue ? (byte)1 : (byte)0 };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeErrorRecord
{
    internal uint StructSize;
    internal uint Category;
    internal uint Detail;
    internal uint RequiredLength;

    internal static NativeErrorRecord Create() => new() { StructSize = 16 };
}
