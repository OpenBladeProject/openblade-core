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
    public ReactiveKeyPulse(byte matrixSlot)
    {
        if (matrixSlot > MaximumMatrixSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(matrixSlot));
        }

        MatrixSlot = matrixSlot;
    }

    public byte MatrixSlot { get; }
}
