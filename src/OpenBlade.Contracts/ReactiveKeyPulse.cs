using System.Text.Json.Serialization;

namespace OpenBlade.Contracts;

/// <summary>
/// A transient request to illuminate one proven PID 02E0 keyboard-matrix slot.
/// The originating key, character, and application are intentionally absent.
/// </summary>
public readonly record struct ReactiveKeyPulse
{
    public const byte MaximumMatrixSlot = 101;

    [JsonConstructor]
    public ReactiveKeyPulse(byte matrixSlot, long? observedTimestamp = null)
    {
        if (matrixSlot > MaximumMatrixSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(matrixSlot));
        }

        MatrixSlot = matrixSlot;
        ObservedTimestamp = observedTimestamp;
    }

    public byte MatrixSlot { get; }

    /// <summary>
    /// Optional monotonic Stopwatch/TimeProvider timestamp captured when the
    /// key packet was observed. Older clients may omit it; the service then
    /// falls back to its render-loop wake timestamp.
    /// </summary>
    public long? ObservedTimestamp { get; }
}
