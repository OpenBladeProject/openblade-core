using System.Runtime.InteropServices;

namespace OpenBlade.Core.Native;

/// <summary>
/// Synchronous pure codec binding. Product loading is admitted by ProductNativeRuntime;
/// explicit artifact loading is retained for offline boundary tests.
/// </summary>
public sealed unsafe class NativeReportCodec : IDisposable
{
    public const uint AbiVersion = 0x00010000;
    public const int ReportLength = 90;
    public const int MaximumPayloadLength = 80;

    private readonly ModuleHandle module;
    private readonly delegate* unmanaged[Cdecl]<uint> version;
    private readonly delegate* unmanaged[Cdecl]<uint, NativeReportRequest*, byte*, uint, byte*, uint, uint*, NativeErrorRecord*, uint> encode;
    private readonly delegate* unmanaged[Cdecl]<uint, NativeReportRequest*, byte*, uint, byte*, uint, NativeValidationOptions*, byte*, uint, uint*, NativeErrorRecord*, uint> validate;

    private NativeReportCodec(ModuleHandle module)
    {
        this.module = module;
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
            nint handle = module.DangerousGetHandle();
            version = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(handle, "ob_abi_version");
            encode = (delegate* unmanaged[Cdecl]<uint, NativeReportRequest*, byte*, uint, byte*, uint, uint*, NativeErrorRecord*, uint>)NativeLibrary.GetExport(handle, "ob_v1_encode_report");
            validate = (delegate* unmanaged[Cdecl]<uint, NativeReportRequest*, byte*, uint, byte*, uint, NativeValidationOptions*, byte*, uint, uint*, NativeErrorRecord*, uint>)NativeLibrary.GetExport(handle, "ob_v1_validate_response");
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }

    /// <summary>Loads only the supplied normalized, fully qualified developer artifact path.</summary>
    public static NativeCodecLoadResult Load(string absoluteLibraryPath)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return LoadFailure(NativeBoundaryErrorKind.UnsupportedPlatform, "The codec supports Windows, Linux, or macOS x64/arm64 processes only.");
        }
        if (!LayoutsMatch())
        {
            return LoadFailure(NativeBoundaryErrorKind.InvalidLayout, "Managed ABI record layout differs from ABI v1.");
        }

        string expectedName = OperatingSystem.IsWindows() ? "openblade_ffi.dll"
            : OperatingSystem.IsMacOS() ? "libopenblade_ffi.dylib" : "libopenblade_ffi.so";
        try
        {
            if (string.IsNullOrWhiteSpace(absoluteLibraryPath)
                || !Path.IsPathFullyQualified(absoluteLibraryPath)
                || !string.Equals(absoluteLibraryPath, Path.GetFullPath(absoluteLibraryPath), StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(absoluteLibraryPath), expectedName, StringComparison.Ordinal)
                || !File.Exists(absoluteLibraryPath))
            {
                return LoadFailure(NativeBoundaryErrorKind.InvalidLibraryPath,
                    $"Supply a normalized absolute path to an existing {expectedName} developer artifact.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return LoadFailure(NativeBoundaryErrorKind.InvalidLibraryPath, exception.Message);
        }

        ModuleHandle? handle = null;
        try
        {
            handle = new ModuleHandle(NativeLibrary.Load(absoluteLibraryPath));
            var codec = new NativeReportCodec(handle);
            uint observed = codec.ReadAbiVersion();
            if (observed != AbiVersion)
            {
                handle.Dispose();
                return LoadFailure(NativeBoundaryErrorKind.AbiMismatch,
                    $"Expected ABI 0x{AbiVersion:X8}; native library reports 0x{observed:X8}.");
            }
            return new(codec, null);
        }
        catch (EntryPointNotFoundException exception)
        {
            handle?.Dispose();
            return LoadFailure(NativeBoundaryErrorKind.MissingExport, exception.Message);
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or FileLoadException)
        {
            handle?.Dispose();
            return LoadFailure(NativeBoundaryErrorKind.LibraryLoadFailure, exception.Message);
        }
    }

    public NativeCodecResult Encode(byte transaction, byte commandClass, byte commandId, ReadOnlySpan<byte> payload)
    {
        NativeReportRequest request = NativeReportRequest.Create(transaction, commandClass, commandId);
        NativeErrorRecord error = NativeErrorRecord.Create();
        byte[] output = new byte[ReportLength];
        uint written = 0;
        try
        {
            fixed (byte* inputPointer = payload)
            fixed (byte* outputPointer = output)
            {
                uint status = RawEncode(AbiVersion, &request, inputPointer, (uint)payload.Length,
                    outputPointer, (uint)output.Length, &written, &error);
                return Interpret(status, error, written, output, ReportLength);
            }
        }
        catch (ObjectDisposedException)
        {
            return Failure(NativeBoundaryErrorKind.Disposed, "The native codec is disposed.");
        }
    }

    /// <summary>Encodes without allocating a report graph; publishes only a verified complete result.</summary>
    public NativeBoundaryError? EncodeInto(byte transaction, byte commandClass, byte commandId,
        ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        if (destination.Length != ReportLength)
        {
            return new(NativeBoundaryErrorKind.InvalidArgument, 2, 0, "A report destination must contain exactly 90 bytes.");
        }
        NativeReportRequest request = NativeReportRequest.Create(transaction, commandClass, commandId);
        NativeErrorRecord error = NativeErrorRecord.Create();
        Span<byte> output = stackalloc byte[ReportLength];
        uint written = 0;
        try
        {
            fixed (byte* inputPointer = payload)
            fixed (byte* outputPointer = output)
            {
                uint status = RawEncode(AbiVersion, &request, inputPointer, (uint)payload.Length,
                    outputPointer, ReportLength, &written, &error);
                if (status == 0 && error.StructSize == 16 && error.Category == 0
                    && error.Detail == 0 && error.RequiredLength == 0 && written == ReportLength)
                {
                    output.CopyTo(destination);
                    return null;
                }
                // The allocating interpretation is used only on failure, preserving the
                // same bounded status/error taxonomy without hot-path allocations.
                return Interpret(status, error, written, new byte[ReportLength], ReportLength).Error
                    ?? InvalidResult().Error;
            }
        }
        catch (ObjectDisposedException)
        {
            return Failure(NativeBoundaryErrorKind.Disposed, "The native codec is disposed.").Error;
        }
    }

    public NativeCodecResult ValidateResponse(byte transaction, byte commandClass, byte commandId,
        ReadOnlySpan<byte> requestPayload, ReadOnlySpan<byte> response, int? expectedPayloadLength = null)
    {
        NativeReportRequest request = NativeReportRequest.Create(transaction, commandClass, commandId);
        NativeValidationOptions options = NativeValidationOptions.Create(expectedPayloadLength);
        NativeErrorRecord error = NativeErrorRecord.Create();
        byte[] output = new byte[MaximumPayloadLength];
        uint written = 0;
        try
        {
            fixed (byte* payloadPointer = requestPayload)
            fixed (byte* responsePointer = response)
            fixed (byte* outputPointer = output)
            {
                uint status = RawValidate(AbiVersion, &request, payloadPointer, (uint)requestPayload.Length,
                    responsePointer, (uint)response.Length, &options, outputPointer, (uint)output.Length, &written, &error);
                NativeCodecResult result = Interpret(status, error, written, output, null);
                if (result.IsSuccess && expectedPayloadLength is int expected && written != expected)
                {
                    return Failure(NativeBoundaryErrorKind.InvalidNativeResult, "Native success did not match the expected payload length.");
                }

                return result;
            }
        }
        catch (ObjectDisposedException)
        {
            return Failure(NativeBoundaryErrorKind.Disposed, "The native codec is disposed.");
        }
    }

    // Internal raw ABI access exists solely for boundary conformance tests. Callers must
    // provide accessible, nonoverlapping ranges; arbitrary pointers cannot be validated.
    internal uint RawEncode(uint abiVersion, NativeReportRequest* request, byte* payload, uint payloadLength,
        byte* output, uint capacity, uint* written, NativeErrorRecord* error)
    {
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
            return encode(abiVersion, request, payload, payloadLength, output, capacity, written, error);
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }

    internal uint RawValidate(uint abiVersion, NativeReportRequest* request, byte* payload, uint payloadLength,
        byte* response, uint responseLength, NativeValidationOptions* options, byte* output, uint capacity,
        uint* written, NativeErrorRecord* error)
    {
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
            return validate(abiVersion, request, payload, payloadLength, response, responseLength, options, output, capacity, written, error);
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }

    private uint ReadAbiVersion()
    {
        bool added = false;
        try
        {
            module.DangerousAddRef(ref added);
            return version();
        }
        finally
        {
            if (added)
            {
                module.DangerousRelease();
            }
        }
    }

    public void Dispose() => module.Dispose();

    internal static bool LayoutsMatch() =>
        sizeof(NativeReportRequest) == 8 && Marshal.SizeOf<NativeReportRequest>() == 8
        && Offset<NativeReportRequest>(nameof(NativeReportRequest.StructSize), 0)
        && Offset<NativeReportRequest>(nameof(NativeReportRequest.Transaction), 4)
        && Offset<NativeReportRequest>(nameof(NativeReportRequest.CommandClass), 5)
        && Offset<NativeReportRequest>(nameof(NativeReportRequest.CommandId), 6)
        && Offset<NativeReportRequest>(nameof(NativeReportRequest.Reserved), 7)
        && sizeof(NativeValidationOptions) == 12 && Marshal.SizeOf<NativeValidationOptions>() == 12
        && Offset<NativeValidationOptions>(nameof(NativeValidationOptions.StructSize), 0)
        && Offset<NativeValidationOptions>(nameof(NativeValidationOptions.ExpectedLength), 4)
        && Offset<NativeValidationOptions>(nameof(NativeValidationOptions.HasExpected), 8)
        && Offset<NativeValidationOptions>(nameof(NativeValidationOptions.Reserved0), 9)
        && Offset<NativeValidationOptions>(nameof(NativeValidationOptions.Reserved1), 10)
        && Offset<NativeValidationOptions>(nameof(NativeValidationOptions.Reserved2), 11)
        && sizeof(NativeErrorRecord) == 16 && Marshal.SizeOf<NativeErrorRecord>() == 16
        && Offset<NativeErrorRecord>(nameof(NativeErrorRecord.StructSize), 0)
        && Offset<NativeErrorRecord>(nameof(NativeErrorRecord.Category), 4)
        && Offset<NativeErrorRecord>(nameof(NativeErrorRecord.Detail), 8)
        && Offset<NativeErrorRecord>(nameof(NativeErrorRecord.RequiredLength), 12);

    private static bool Offset<T>(string field, int expected) where T : struct
        => Marshal.OffsetOf<T>(field).ToInt64() == expected;

    internal static NativeCodecResult Interpret(uint status, NativeErrorRecord error, uint written,
        byte[] output, int? exactLength)
    {
        if (error.StructSize != 16 || status > 5 || error.Category != status)
        {
            return InvalidResult();
        }

        if (status == 0)
        {
            if (error.Detail != 0 || error.RequiredLength != 0 || written > output.Length
                || (exactLength.HasValue && written != exactLength.Value))
            {
                return InvalidResult();
            }

            return new(output.AsSpan(0, (int)written).ToArray(), null);
        }
        bool detailValid = status switch
        {
            1 or 3 or 5 => error.Detail == 0,
            2 => error.Detail is >= 1 and <= 6,
            4 => error.Detail is >= 1 and <= 7,
            _ => false,
        };
        if (!detailValid || (status == 3
                ? error.RequiredLength <= output.Length || written != error.RequiredLength
                : written != 0 || error.RequiredLength != 0))
        {
            return InvalidResult();
        }

        return new(null, new((NativeBoundaryErrorKind)status, error.Detail, error.RequiredLength,
            $"Native codec failed: category {status}, detail {error.Detail}."));
    }

    private static NativeCodecResult InvalidResult()
        => Failure(NativeBoundaryErrorKind.InvalidNativeResult, "Native codec returned an inconsistent status, error record, or output length.");
    private static NativeCodecResult Failure(NativeBoundaryErrorKind kind, string message)
        => new(null, new(kind, 0, 0, message));
    private static NativeCodecLoadResult LoadFailure(NativeBoundaryErrorKind kind, string message)
        => new(null, new(kind, 0, 0, message));

    private sealed class ModuleHandle : SafeHandle
    {
        internal ModuleHandle(nint handle) : base(nint.Zero, ownsHandle: true) => SetHandle(handle);
        public override bool IsInvalid => handle == nint.Zero;
        protected override bool ReleaseHandle()
        {
            NativeLibrary.Free(handle);
            return true;
        }
    }
}
