using System.Reflection;
using System.Security.Cryptography;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ProductionLightingPortTests
{
    [Fact]
    public void FireAdmission_ExtendsSoftwareEffectsWithoutChangingCapturedWireEvidence()
    {
        ProductionLightingSemanticAdmissionEvidence approved =
            ProductionLightingEvidenceSource.ReadValidatedSemanticAdmission();
        ProductionLightingSemanticAdmissionEvidence current =
            ProductionLightingEvidenceSource.BuildCurrentSemanticAdmission();
        Assert.Contains(LightingEffect.Fire, approved.SupportedEffects);
        ProductionLightingSoftwareEffectEvidence fire = Assert.Single(approved.SoftwareEffects);
        Assert.Equal(LightingEffect.Fire, fire.Effect);
        Assert.Equal("SoftwarePatternWithoutPhysicalValidation", fire.Basis);
        Assert.Equal("Pending", fire.PhysicalValidation);
        Assert.Equal(nameof(SoftwareFirePattern), fire.Implementation);
        Assert.True(ProductionLightingEvidenceSource.SemanticAdmissionMatches(approved, current));
        Assert.False(ProductionLightingEvidenceSource.SemanticAdmissionMatches(approved,
            current with { SupportedEffects = current.SupportedEffects.Where(effect => effect != LightingEffect.Fire).ToArray() }));
        Assert.False(ProductionLightingEvidenceSource.SemanticAdmissionMatches(approved,
            current with { SoftwareEffects = [fire with { PhysicalValidation = "Passed" }] }));
        Assert.False(ProductionLightingEvidenceSource.SemanticAdmissionMatches(approved,
            current with { SoftwareEffects = [fire with { Implementation = "AnotherModelOracle" }] }));
        Assert.Equal("7AFEE96E76C0547BE237465CAA2149E9254BD71DC1DE8D3EC17E5F2F0976C98F",
            current.Matrix.KnownAnswerFeatureReportSha256);
        Assert.Equal("187711408FC0667273CABB19A1CB73B57CA53F98688993BA76E7352A8F2AE0FB",
            current.Matrix.SourceLayoutSha256);
        using Stream oracle = typeof(ProductionLightingEvidenceSource).Assembly.GetManifestResourceStream(
            ProductionLightingEvidenceSource.OracleResourceName)!;
        Assert.Equal("5177FA9F6F91D96C9C34C5506CDA8EDC0A86EE5B5EEFA5F4322F4E4633C23521",
            Convert.ToHexString(SHA256.HashData(oracle)));
    }

    [Fact]
    public async Task Fire_DarkWakeUsesOwnRegionalProjectionBeforeRaisingBrightness()
    {
        var session = new LightingSession(initialBrightness: 0) { Transition86Response = [3, 129, 7] };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true, timeProvider: new ManualTimeProvider());
        writer.WriteObserved = () => Assert.Equal(0, session.Brightness);

        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), default);

        Pid02E0MatrixFrame expected = Pid02E0RegionalFrameProjection.Project(
            new Pid02E0FireEffect().RenderFrame(TimeSpan.Zero), 3);
        Assert.Equal(expected.ToArray(), Assert.Single(writer.Reports).Frame.ToArray());
        Assert.NotEqual(MatrixRgb.Black, writer.Reports[0].Frame[48]);
        Assert.NotEqual(MatrixRgb.Black, writer.Reports[0].Frame[64]);
        Assert.NotEqual(MatrixRgb.Black, writer.Reports[0].Frame[70]);
        Assert.Equal(MatrixRgb.Black, writer.Reports[0].Frame[49]);
        Assert.Equal(MatrixRgb.Black, writer.Reports[0].Frame[66]);
        Assert.Equal(128, session.Brightness);
        Assert.Equal(LightingRendererOwnershipState.Fire, port.OwnershipState);
        Assert.Null(await port.ReadStateAsync(default));
        Assert.Equal(LightingRendererOwnershipState.Fire, port.OwnershipState);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0F);
        Assert.Equal((0x03, 0x83, "010500"), CommandTuple(session.Commands.Last()));

        Assert.Equal(LightingRendererOwnershipState.Fire, await port.RelinquishRendererAsync(default));
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task Fire_FailedDarkWakeNeverRaisesBrightnessOrClaimsOwnership()
    {
        var session = new LightingSession(initialBrightness: 0);
        var writer = new MatrixWriter();
        writer.FailNextWrite();
        await using var port = CreatePort(session, writer, admitted: true, timeProvider: new ManualTimeProvider());

        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(LightingSettingsFactory.Fire(50), default));

        Assert.Equal(0, session.Brightness);
        Assert.Equal(0, session.BrightnessSetCalls);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task Fire_StreamFailureFaultsOwnershipWithoutReplaying()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        var time = new ManualTimeProvider();
        await using var port = CreatePort(session, writer, admitted: true, timeProvider: time);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(100), default);
        Task stream = Assert.IsAssignableFrom<Task>(typeof(ProductionLightingPort)
            .GetField("streamTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(port));
        int before = writer.Reports.Count;
        writer.FailNextWrite();

        time.Advance(Pid02E0LightingTiming.AnimatedFrameInterval);
        await stream.WaitAsync(TestDeadlockGuard);

        Assert.Equal(before + 1, writer.Reports.Count);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.Equal(LightingRendererOwnershipState.Faulted, await port.InvalidateAsync());
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }
}
