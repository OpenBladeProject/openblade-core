using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Pure encoding of observed configured demand; no transport or production admission.
/// An exact echo acknowledges the payload, not fan speed, state, or ownership.
/// </summary>
internal static class CoolingPadHostDemandProtocol
{
    public static RazerReport CreateSet(
        byte transactionId,
        CoolingPadConfiguredRpmDemand demand) =>
        RazerReport.Create(transactionId, 0x0D, 0x01, CreatePayload(demand));

    public static RazerCommandSpec CreateSpec(CoolingPadConfiguredRpmDemand demand) =>
        new(0x0D, 0x01, CreatePayload(demand), 3,
            payload => IsExactSetEcho(payload.Span, demand));

    public static bool IsExactSetEcho(
        ReadOnlySpan<byte> payload,
        CoolingPadConfiguredRpmDemand expectedDemand) =>
        expectedDemand.IsWellFormed
        && payload.Length == 3
        && payload[0] == 0x01
        && payload[1] == 0x05
        && payload[2] == expectedDemand.Rpm / 50;

    private static byte[] CreatePayload(CoolingPadConfiguredRpmDemand demand)
    {
        if (!demand.IsWellFormed)
        {
            throw new ArgumentOutOfRangeException(nameof(demand));
        }

        return [0x01, 0x05, checked((byte)(demand.Rpm / 50))];
    }
}
