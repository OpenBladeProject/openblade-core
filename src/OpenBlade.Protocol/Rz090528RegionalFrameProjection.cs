using System.Security.Cryptography;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class Rz090528RegionalFrameProjection
{
    private static readonly RegionalKeyboardFrameProjection Projection = new(
        Blade16AmdKeyboardLightingLayout.CanvasHoleSlots,
        Blade16AmdKeyboardLightingLayout.TryMapCanvasSlot,
        (slot, layout) => Blade16AmdKeyboardLightingLayout.IsKeySlot(slot, layout));

    internal static Rz090528MatrixFrame Project(Rz090528MatrixFrame canvas, byte layoutId) =>
        Rz090528MatrixFrame.FromOwnedSlots(
            Projection.Project(canvas.AsSpan(), layoutId, canvas.LogicalKeyColors),
            canvas.LogicalKeyColors);

    // SO6110's own source report and all 256 encoded row/latch sequences.
    // The factory's existing exact host/HID checks remain independent.
    internal static bool IsSourceContractApproved() =>
        Rz090528MatrixFrame.RowCount == 6
        && Rz090528MatrixFrame.ColumnCount == 17
        && Rz090528MatrixRowProtocol.RowPayloadLength == 55
        && Blade16AmdKeyboardLightingLayout.SourceFixtureSha256
            == "F8F0BA8D85804187B3B063363460BC3E867BC1A147D9AE2895970A62DE1A1013"
        && SemanticFingerprint() == "F902BA6F27945ADE8C70CF16F6C3308DCA6ACCD69B5648121C7E2C22090DDC15";

    private static readonly Lazy<string> Fingerprint = new(ComputeSemanticFingerprint);

    internal static string SemanticFingerprint() => Fingerprint.Value;

    private static string ComputeSemanticFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var colors = new RgbColor[102];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            colors[slot] = new RgbColor((byte)(slot + 1), (byte)(255 - slot), (byte)(slot ^ 0x5A));
        }
        for (int layout = 0; layout < 256; layout++)
        {
            for (int kind = 0; kind < 2; kind++)
            {
                var canvas = new Rz090528MatrixFrame(colors, logicalKeyColors: kind == 1);
                Rz090528MatrixFrame physical = Project(canvas, (byte)layout);
                hash.AppendData(new byte[] { (byte)kind });
                for (int row = 0; row < 6; row++)
                {
                    hash.AppendData(new byte[] { Rz090528MatrixRowProtocol.CommandClass, Rz090528MatrixRowProtocol.RowCommandId });
                    hash.AppendData(Rz090528MatrixRowProtocol.EncodeRow(row, physical));
                }
                hash.AppendData(new byte[] { Rz090528MatrixRowProtocol.CommandClass, Rz090528MatrixRowProtocol.LatchCommandId });
                hash.AppendData(Rz090528MatrixRowProtocol.LatchArguments.Span);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
