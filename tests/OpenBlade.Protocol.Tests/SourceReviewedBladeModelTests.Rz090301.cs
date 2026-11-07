using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    [Fact]
    public async Task Rz090301_GpuWattageLevelsUseReviewedSelectorBytesAndIndependentReadback()
    {
        var session = new Session();
        await using var port = Create(0x0245, session);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.Medium);
        await port.SetCustomPerformanceAsync(requested, default);
        Assert.Equal(requested, (await port.QueryPerformanceFanStateAsync(default)).CustomPerformance);
        RazerCommandSpec[] levels = session.Commands.Where(command => command.CommandId == 0x07).ToArray();
        Assert.Equal(2, levels.Length);
        Assert.Equal(new byte[] { 1, 1, 2 }, levels[0].Arguments.ToArray());
        Assert.Equal(new byte[] { 1, 2, 1 }, levels[1].Arguments.ToArray());
        Assert.All(levels, command => Assert.Equal(0x0D, command.ClassId));
        Assert.Contains(session.Commands, command => command.CommandId == 0x87
            && command.Arguments.Span.SequenceEqual(new byte[] { 1, 2, 0 }));
        AssertFreshAdapterBeforeSetters(session);
        Assert.Contains(ExperimentalBladeModels.FeatureEvidenceFor(Model(0x0245)),
            item => item.Feature == "CustomGpuWattageLevels0To2"
                && item.Basis == "SynapseSourceBackedWithoutPhysicalValidation");
    }

    [Fact]
    public async Task Rz090301_IdentityLostBeforeGpuSetterPausesWithoutReplayAndRecoversReadOnly()
    {
        var session = new Session { Mode = 4 };
        bool identity = true;
        session.BeforeSubmission = command =>
        {
            if (command.ClassId == 0x0D && command.CommandId == 0x07
                && command.Arguments.Span[1] == 2)
            {
                identity = false;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy, identity: () => identity);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.High);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetCustomPerformanceAsync(requested, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Commands, command => command.CommandId == 0x07);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(requested, default));
        Assert.Equal(before, session.Commands.Count);
        identity = true;
        ConfirmedDeviceState recovered = await port.ReadRecoveryStateAsync(default);
        Assert.Equal(new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.Low),
            recovered.CustomPerformance);
        Assert.Null(recovered.Battery);
        Assert.False(policy.WritesAllowed);
        Assert.All(session.Commands.Skip(before), command => Assert.True(command.CommandId >= 0x80));
    }

    [Fact]
    public async Task Rz090301_StatusOnlySuccessWithStaleCustomReadbackPausesWithoutReplay()
    {
        var session = new Session { Mode = 4, IgnoreWrites = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.Medium);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetCustomPerformanceAsync(requested, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.Equal(before, session.Commands.Count);
    }
}
