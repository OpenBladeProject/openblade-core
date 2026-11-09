using OpenBlade.Core.Native;

namespace OpenBlade.Protocol;

internal enum RazerCommandStatus : byte
{
    New = 0x00,
    Successful = 0x02,
    NotSupported = 0x05,
}

internal enum DeviceStateQuery
{
    BatteryProtection,
    TemporaryFullCharge,
    PerformanceAndFans,
    ThermalChannelOne,
    ThermalChannelTwo,
    ThermalChannelAgreement,
    CustomCpuPerformance,
    CustomGpuPerformance,
    CustomPerformanceCombination,
    KeyboardBrightness,
    LogoLightingState,
    LogoLightingEffect,
    LogoLightingCombination,
    KeyboardLightingEffect,
    KeyboardLightingCombination,
}

public sealed class RazerProtocolException : Exception
{
    public RazerProtocolException(
        string message,
        HidProtocolValidationFailure validationFailure)
        : base(message)
    {
        ValidationFailure = validationFailure;
    }

    public HidProtocolValidationFailure ValidationFailure { get; }

    internal DeviceStateQuery? StateQuery { get; init; }

    internal string? RejectedPayloadHex { get; init; }

    internal HidSessionFailureDiagnostic? Diagnostic { get; init; }

    internal Task? NativeRetirement { get; init; }

    // Present only when the combined state read completed every other getter
    // after a content rejection. Callers that do not consume it keep the
    // fail-closed whole-read behavior of an ordinary protocol exception.
    internal PartialDeviceStateRead? PartialState { get; init; }
}

internal sealed class RazerReport
{
    public const int ReportLength = 90;
    public const int MaximumPayloadLength = 80;

    private readonly byte[] payloadBytes;

    private RazerReport(byte transactionId, byte commandClass, byte commandId, byte[] payload)
    {
        TransactionId = transactionId;
        CommandClass = commandClass;
        CommandId = commandId;
        payloadBytes = payload;
    }

    public byte TransactionId { get; }

    public byte CommandClass { get; }

    public byte CommandId { get; }

    public static RazerReport Create(
        byte transactionId,
        byte commandClass,
        byte commandId,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"Razer payloads cannot exceed {MaximumPayloadLength} bytes.");
        }

        return new RazerReport(transactionId, commandClass, commandId, payload.ToArray());
    }

    public byte[] Encode()
    {
        byte[] bytes = new byte[ReportLength];
        Encode(bytes);
        return bytes;
    }

    internal void Encode(Span<byte> destination)
    {
        if (destination.Length != ReportLength)
        {
            throw new ArgumentException(
                $"A Razer report destination must contain exactly {ReportLength} bytes.",
                nameof(destination));
        }

        Encode(TransactionId, CommandClass, CommandId, payloadBytes, destination);
    }

    internal static void Encode(
        byte transactionId,
        byte commandClass,
        byte commandId,
        ReadOnlySpan<byte> payload,
        Span<byte> destination)
    {
        if (payload.Length > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"Razer payloads cannot exceed {MaximumPayloadLength} bytes.");
        }

        if (destination.Length != ReportLength)
        {
            throw new ArgumentException(
                $"A Razer report destination must contain exactly {ReportLength} bytes.",
                nameof(destination));
        }

        // The managed static encoder cleared the destination and filled its header before copying
        // payload. For aliased spans, those writes were observable as input. Preserve
        // that legacy input projection without overlapping ABI buffers or publishing
        // partial native output. Production command buffers normally do not alias.
        Span<byte> projected = stackalloc byte[MaximumPayloadLength];
        scoped ReadOnlySpan<byte> nativePayload = payload;
        if (payload.Overlaps(destination, out int destinationOffset))
        {
            payload.CopyTo(projected);
            int first = Math.Max(0, destinationOffset);
            int end = Math.Min(payload.Length, destinationOffset + ReportLength);
            for (int index = first; index < end; index++)
            {
                projected[index] = (index - destinationOffset) switch
                {
                    1 => transactionId,
                    5 => (byte)payload.Length,
                    6 => commandClass,
                    7 => commandId,
                    _ => 0,
                };
            }
            nativePayload = projected[..payload.Length];
        }
        NativeBoundaryError? error = ProductNativeRuntime.Codec.EncodeInto(
            transactionId, commandClass, commandId, nativePayload, destination);
        if (error is not null)
        {
            throw new NativeComponentException(error);
        }
    }

    public ReadOnlyMemory<byte> ValidateResponse(
        ReadOnlySpan<byte> response,
        int? expectedPayloadLength = null)
    {
        NativeCodecResult result = ProductNativeRuntime.Codec.ValidateResponse(
            TransactionId, CommandClass, CommandId, payloadBytes, response, expectedPayloadLength);
        return InterpretResponse(result, response);
    }

    // The typed mapping is independently exercised with synthetic boundary failures;
    // it never retries a call or interprets a failed native output as a response.
    internal static ReadOnlyMemory<byte> InterpretResponse(NativeCodecResult result, ReadOnlySpan<byte> response)
    {
        if (result.IsSuccess)
        {
            return result.Output!;
        }

        NativeBoundaryError error = result.Error!;
        if (error.Kind != NativeBoundaryErrorKind.ProtocolValidation)
        {
            throw new NativeComponentException(error);
        }

        HidProtocolValidationFailure failure = (HidProtocolValidationFailure)error.Detail;
        string message = failure switch
        {
            HidProtocolValidationFailure.EnvelopeLength => $"Expected a {ReportLength}-byte response.",
            HidProtocolValidationFailure.Checksum => "Response checksum is invalid.",
            HidProtocolValidationFailure.TransactionMismatch => "Response transaction does not match the request.",
            HidProtocolValidationFailure.CommandClassMismatch => "Response command class does not match the request.",
            HidProtocolValidationFailure.CommandIdMismatch => "Response command ID does not match the request.",
            HidProtocolValidationFailure.Status => $"Razer command failed with status 0x{response[0]:X2}.",
            HidProtocolValidationFailure.PayloadLength => response[5] > MaximumPayloadLength
                ? "Response payload length is invalid."
                : "Response payload length does not match the command contract.",
            _ => throw new NativeComponentException(error),
        };
        throw new RazerProtocolException(message, failure);
    }
}
