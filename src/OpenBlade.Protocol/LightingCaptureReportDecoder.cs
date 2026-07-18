namespace OpenBlade.Protocol;

internal enum LightingCaptureSemantic
{
    BrightnessSet,
    BrightnessQuery,
    CustomMatrixModeEntry,
    ReferenceHardwareEffectOffCandidate,
    ReferenceHardwareEffectWaveCandidate,
    ReferenceHardwareEffectSpectrumCandidate,
    ReferenceHardwareEffectStaticCandidate,
    UnknownLightingCommand,
}

internal readonly record struct LightingCaptureObservation(
    byte Status,
    byte TransactionId,
    byte CommandId,
    ReadOnlyMemory<byte> Payload,
    LightingCaptureSemantic Semantic)
{
    internal bool IsSuccessfulResponse => Status == (byte)RazerCommandStatus.Successful;

    internal bool IsIndependentBrightnessReadback =>
        IsSuccessfulResponse && Semantic == LightingCaptureSemantic.BrightnessQuery;
}

/// <summary>
/// Decodes already-captured class-03 feature reports. This type deliberately
/// has no report-construction or transport surface.
/// </summary>
internal static class LightingCaptureReportDecoder
{
    private const byte LightingCommandClass = 0x03;
    private const byte BrightnessSetCommand = 0x03;
    private const byte BrightnessQueryCommand = 0x83;
    private const byte KeyboardEffectCommand = 0x0A;

    internal static bool TryDecode(
        ReadOnlySpan<byte> report,
        out LightingCaptureObservation observation)
    {
        observation = default;
        if (report.Length != RazerReport.ReportLength
            || report[0] is not ((byte)RazerCommandStatus.New)
                and not ((byte)RazerCommandStatus.Successful)
            || report[2] != 0
            || report[3] != 0
            || report[4] != 0
            || report[5] > RazerReport.MaximumPayloadLength
            || report[6] != LightingCommandClass
            || report[89] != 0
            || report[88] != CalculateChecksum(report))
        {
            return false;
        }

        int payloadLength = report[5];
        byte[] payload = report.Slice(8, payloadLength).ToArray();
        LightingCaptureSemantic semantic = Classify(report[7], payload);
        observation = new LightingCaptureObservation(
            report[0],
            report[1],
            report[7],
            payload,
            semantic);
        return true;
    }

    private static LightingCaptureSemantic Classify(byte commandId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 3 && payload[0] == 0x01 && payload[1] == 0x05)
        {
            return commandId switch
            {
                BrightnessSetCommand => LightingCaptureSemantic.BrightnessSet,
                BrightnessQueryCommand => LightingCaptureSemantic.BrightnessQuery,
                _ => LightingCaptureSemantic.UnknownLightingCommand,
            };
        }

        // PID 02E0 captures use this short operation before starting the
        // proprietary 374-byte host-rendered matrix stream. It does not select
        // Static, Spectrum, Breathing, or Reactive by itself.
        if (commandId == KeyboardEffectCommand
            && payload.Length == 2
            && payload[0] == 0x05
            && payload[1] == 0x00)
        {
            return LightingCaptureSemantic.CustomMatrixModeEntry;
        }

        // Fang documents class 03 / command 0A with a full 80-byte payload as
        // a hardware-effect setter on other Blade generations. PID 02E0 has
        // not validated that interpretation, so retain it only as a reference
        // candidate for capture comparison.
        if (commandId == KeyboardEffectCommand && payload.Length == 80)
        {
            return payload[0] switch
            {
                0x00 when IsZeroFilled(payload[1..]) =>
                    LightingCaptureSemantic.ReferenceHardwareEffectOffCandidate,
                0x01 when payload[1] is 0x01 or 0x02 && IsZeroFilled(payload[2..]) =>
                    LightingCaptureSemantic.ReferenceHardwareEffectWaveCandidate,
                0x04 when IsZeroFilled(payload[1..]) =>
                    LightingCaptureSemantic.ReferenceHardwareEffectSpectrumCandidate,
                0x06 when IsZeroFilled(payload[4..]) =>
                    LightingCaptureSemantic.ReferenceHardwareEffectStaticCandidate,
                _ => LightingCaptureSemantic.UnknownLightingCommand,
            };
        }

        return LightingCaptureSemantic.UnknownLightingCommand;
    }

    private static bool IsZeroFilled(ReadOnlySpan<byte> payload)
    {
        foreach (byte value in payload)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static byte CalculateChecksum(ReadOnlySpan<byte> report)
    {
        byte checksum = 0;
        for (int index = 2; index <= 87; index++)
        {
            checksum ^= report[index];
        }

        return checksum;
    }
}
