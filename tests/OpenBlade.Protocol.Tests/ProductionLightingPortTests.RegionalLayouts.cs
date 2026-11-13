using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ProductionLightingPortTests
{
    [Theory]
    [InlineData(3, 129, 7)]
    [InlineData(1, 128, 1)]
    [InlineData(12, 0, 0)]
    public async Task KeyboardLayoutQuery_AcceptsVendorRegionalMetadata(byte layout, byte edition, byte firmwareId)
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
    public async Task KeyboardLayoutQuery_RejectsMalformedReplyBeforeActivatingOrSubmittingFrame(int length)
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            Transition86Response = new byte[length],
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);

        await Assert.ThrowsAnyAsync<IOException>(() => port.SetLightingAsync(
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
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(1)]
    public async Task Rz090528RegionalStatic_UsesItsOwnSourceMap(byte layout)
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            Transition86Response = [layout, 129, 17],
        };
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(session, new BatteryCommands(), new TemporaryCommands()),
            TimeSpan.FromSeconds(1), staticEffectAdmitted: true);
        var color = new RgbColor(12, 34, 56);

        await port.SetLightingAsync(LightingSettingsFactory.Static(color, 100), default);

        RazerCommandSpec[] rows = session.Commands.Where(c => c.ClassId == 3 && c.CommandId == 11).ToArray();
        Assert.Equal(6, rows.Length);
        for (int slot = 0; slot < 102; slot++)
        {
            ReadOnlySpan<byte> row = rows[slot / 17].Arguments.Span;
            int offset = 4 + 3 * (slot % 17);
            var actual = new RgbColor(row[offset], row[offset + 1], row[offset + 2]);
            Assert.Equal(Blade16AmdKeyboardLightingLayout.IsKeySlot(slot, layout) ? color : default, actual);
        }
        Assert.True(Rz090528RegionalFrameProjection.IsSourceContractApproved());
    }

    [Fact]
    public void Rz090528RegionalProjection_PreservesLegacyRightAltOnEverySourceLayout()
    {
        var colors = new RgbColor[102];
        colors[91] = new RgbColor(1, 2, 3);
        colors[66] = new RgbColor(4, 5, 6);
        colors[49] = new RgbColor(7, 8, 9);
        var canvas = new Rz090528MatrixFrame(colors, logicalKeyColors: true);
        for (int layout = 0; layout < 256; layout++)
        {
            Rz090528MatrixFrame physical = Rz090528RegionalFrameProjection.Project(canvas, (byte)layout);
            Assert.True(Blade16AmdKeyboardLightingLayout.TryMapCanvasSlot(91, (byte)layout, out byte alt));
            Assert.Equal(colors[91], physical[alt]);
            Assert.False(Blade16AmdKeyboardLightingLayout.TryMapCanvasSlot(94, (byte)layout, out _));
            for (int slot = 0; slot < 102; slot++)
            {
                if (!Blade16AmdKeyboardLightingLayout.IsKeySlot(slot, (byte)layout))
                {
                    Assert.Equal(default, physical[slot]);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayoutMetadata_IsReadOncePerSessionAndNeverForBrightnessOnly(bool amd)
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            Transition86Response = [12, 129, 255],
        };
        await using IProductionLightingPort port = amd
            ? new Rz090528BrightnessLightingPort(
                new ValidationActor(session, new BatteryCommands(), new TemporaryCommands()),
                TimeSpan.FromSeconds(1), staticEffectAdmitted: true)
            : CreatePort(session, new MatrixWriter(), admitted: true);
        await port.StepBrightnessAsync(KeyboardBrightnessDirection.Increase, default);
        Assert.DoesNotContain(session.Commands, c => c.ClassId == 0 && c.CommandId == 0x86);
        await port.SetLightingAsync(LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100), default);
        await port.SetLightingAsync(LightingSettingsFactory.Static(new RgbColor(4, 5, 6), 100), default);
        Assert.Single(session.Commands, c => c.ClassId == 0 && c.CommandId == 0x86);
    }
    [Theory]
    [InlineData(70, 3, 70)]
    [InlineData(31, 12, 31)]
    [InlineData(82, 12, 82)]
    [InlineData(64, 12, 94)]
    [InlineData(93, 12, 93)]
    [InlineData(48, 12, 90)]
    public void SupplementalKeyPulses_ReachTheirExactSourceSlotForReactiveAndRipple(
        byte canvas, byte layout, byte physical)
    {
        var color = new MatrixRgb(17, 37, 59);
        var reactive = new Pid02E0ReactiveEffect(color, TimeSpan.FromSeconds(1));
        reactive.Pulse(canvas, TimeSpan.Zero);
        Pid02E0MatrixFrame frame = Pid02E0RegionalFrameProjection.Project(
            reactive.RenderFrame(TimeSpan.Zero), layout);
        Assert.Equal(color, frame[physical]);
        Assert.Single(frame.ToArray(), c => c != MatrixRgb.Black);
        var ripple = new Pid02E0RippleEffect(color);
        ripple.Pulse(canvas, TimeSpan.Zero);
        Assert.Equal(color, Pid02E0RegionalFrameProjection.Project(
            ripple.RenderFrame(TimeSpan.Zero), layout)[physical]);

        var rgb = new RgbColor(17, 37, 59);
        var amdReactive = new Rz090528ReactiveEffect(rgb, TimeSpan.FromSeconds(1));
        amdReactive.Pulse(canvas, TimeSpan.Zero);
        Assert.Equal(rgb, Rz090528RegionalFrameProjection.Project(
            amdReactive.RenderFrame(TimeSpan.Zero), layout)[physical]);
        var amdRipple = new Rz090528RippleEffect(rgb);
        amdRipple.Pulse(canvas, TimeSpan.Zero);
        Assert.Equal(rgb, Rz090528RegionalFrameProjection.Project(
            amdRipple.RenderFrame(TimeSpan.Zero), layout)[physical]);
    }
}
