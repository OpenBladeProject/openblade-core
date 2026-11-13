using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ProductionLightingPortTests
{
    [Theory]
    [InlineData(3, 129, 7)]
    [InlineData(1, 128, 1)]
    [InlineData(12, 0, 0)]
    public async Task EditionQuery_AcceptsVendorRegionalMetadata(byte layout, byte edition, byte firmwareId)
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            Transition86Response = [layout, edition, firmwareId],
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);

        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100), default);

        Assert.Single(writer.Reports);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task EditionQuery_RejectsMalformedReplyBeforeActivatingOrSubmittingFrame(int length)
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            Transition86Response = new byte[length],
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);

        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100), default));

        Assert.Empty(writer.Reports);
        Assert.DoesNotContain(session.Commands,
            command => command.ClassId == 0x03 && command.CommandId == 0x0A);
    }
    [Fact]
    public async Task GermanStatic_UsesSourceEnterBackslashAndIsoKeySlots()
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            Transition86Response = [3, 129, 7],
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        var color = new RgbColor(0x12, 0x34, 0x56);

        await port.SetLightingAsync(LightingSettingsFactory.Static(color, 100), default);

        Pid02E0MatrixFrame physical = Assert.Single(writer.Reports).Frame;
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), physical[48]);
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), physical[64]);
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), physical[70]);
        Assert.Equal(MatrixRgb.Black, physical[49]);
        Assert.Equal(MatrixRgb.Black, physical[66]);
    }

    [Fact]
    public async Task GermanSessionFrame_ProjectsTypedCanvasAtTheSameBoundary()
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            Transition86Response = [3, 129, 7],
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Right, 100), default);
        var colors = new RgbColor[AmbientLightingFrame.SlotCount];
        colors[66] = new RgbColor(7, 8, 9);
        colors[49] = new RgbColor(10, 11, 12);

        await port.PushAmbientLightingFrameAsync(new AmbientLightingFrame(colors), default);

        Assert.Equal(new MatrixRgb(7, 8, 9), writer.Reports[^1].Frame[48]);
        Assert.Equal(new MatrixRgb(10, 11, 12), writer.Reports[^1].Frame[64]);
        Assert.Equal(MatrixRgb.Black, writer.Reports[^1].Frame[49]);
        Assert.Equal(MatrixRgb.Black, writer.Reports[^1].Frame[66]);
    }

    [Fact]
    public void RegionalProjection_PreservesLogicalKeysAndMasksEverySourceLayout()
    {
        var colors = new MatrixRgb[102];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            if (!AmbientLightingFrame.IsHole(slot))
            {
                colors[slot] = new MatrixRgb((byte)(slot + 1), 17, 29);
            }
        }
        var canvas = new Pid02E0MatrixFrame(colors, logicalKeyColors: true);
        for (int layout = 0; layout < 256; layout++)
        {
            Pid02E0MatrixFrame physical = Pid02E0RegionalFrameProjection.Project(canvas, (byte)layout);
            for (byte slot = 0; slot < colors.Length; slot++)
            {
                if (!AmbientLightingFrame.IsHole(slot)
                    && Blade16KeyboardLightingLayout.TryMapCanvasSlot(slot, (byte)layout, out byte target))
                {
                    Assert.Equal(colors[slot], physical[target]);
                }
                if (!Blade16KeyboardLightingLayout.IsKeySlot(slot, (byte)layout))
                {
                    Assert.Equal(MatrixRgb.Black, physical[slot]);
                }
            }
        }
    }

    [Fact]
    public void RegionalProjection_PerKeyContentLeavesUnspecifiedRegionalKeysBlack()
    {
        var colors = new MatrixRgb[102];
        colors[66] = new MatrixRgb(10, 20, 30);
        colors[49] = new MatrixRgb(40, 50, 60);
        Pid02E0MatrixFrame physical = Pid02E0RegionalFrameProjection.Project(
            new Pid02E0MatrixFrame(colors, logicalKeyColors: true), 3);

        Assert.Equal(colors[66], physical[48]);
        Assert.Equal(colors[49], physical[64]);
        Assert.Equal(MatrixRgb.Black, physical[70]);
        Assert.Equal(MatrixRgb.Black, physical[49]);
        Assert.Equal(MatrixRgb.Black, physical[66]);
    }

    [Fact]
    public void RegionalRoutingAdmission_RejectsAChangedSourceOrProjection()
    {
        var approved = ProductionLightingEvidenceSource.ReadValidatedSemanticAdmission();
        var current = ProductionLightingEvidenceSource.BuildCurrentSemanticAdmission();
        Assert.Equal(Pid02E0RegionalFrameProjection.SemanticFingerprint(), current.Matrix.RegionalProjectionSha256);
        Assert.False(ProductionLightingEvidenceSource.SemanticAdmissionMatches(approved,
            current with { Matrix = current.Matrix with { SourceLayoutSha256 = new string('0', 64) } }));
        Assert.False(ProductionLightingEvidenceSource.SemanticAdmissionMatches(approved,
            current with { Matrix = current.Matrix with { RegionalProjectionSha256 = new string('0', 64) } }));
    }
}
