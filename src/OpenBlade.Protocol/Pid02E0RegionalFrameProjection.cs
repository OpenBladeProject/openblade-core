using System.Security.Cryptography;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Projects the saved logical canvas onto the exact 02E0 MCU map.
/// Source arrays own key addresses. Sampling a regional-only spatial cell is
/// an OpenBlade rendering choice and never establishes an input binding.</summary>
internal static class Pid02E0RegionalFrameProjection
{
    private static readonly RegionalKeyboardFrameProjection Projection = new(
        Blade16KeyboardLightingLayout.CanvasHoleSlots,
        Blade16KeyboardLightingLayout.TryMapCanvasSlot,
        (slot, layout) => Blade16KeyboardLightingLayout.IsKeySlot(slot, layout));

    internal static Pid02E0MatrixFrame Project(Pid02E0MatrixFrame canvas, byte layoutId)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        return new Pid02E0MatrixFrame(
            Projection.Project(canvas.AsSpan(), layoutId, canvas.LogicalKeyColors),
            canvas.LogicalKeyColors);
    }

    // A distinct color per logical slot binds source routing, actual regional
    // holes and complete report encoding. A uniform answer alone misses aliases.
    private static readonly Lazy<string> Fingerprint = new(ComputeSemanticFingerprint);

    internal static string SemanticFingerprint() => Fingerprint.Value;

    private static string ComputeSemanticFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var colors = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            colors[slot] = new MatrixRgb((byte)(slot + 1), (byte)(255 - slot), (byte)(slot ^ 0x5A));
        }
        for (int layout = 0; layout < 256; layout++)
        {
            for (int kind = 0; kind < 2; kind++)
            {
                var canvas = new Pid02E0MatrixFrame(colors, logicalKeyColors: kind == 1);
                byte[] report = Pid02E0MatrixReportCodec.EncodeFeatureReportBuffer(
                    new Pid02E0MatrixReport(0x5A, Project(canvas, (byte)layout)));
                hash.AppendData(new byte[] { (byte)kind });
                hash.AppendData(report);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
