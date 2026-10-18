using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeCustomTests
{
    [Theory]
    [InlineData(2, 2, 3, 1)]
    [InlineData(3, 1, 2, 2)]
    public async Task SourceBackedPairs_RoundTripWithoutPresetResetOrBridge(
        byte cpu, byte gpu, byte targetCpu, byte targetGpu)
    {
        var session = new Session { Mode = 4, Cpu = cpu, Gpu = gpu };
        var policy = Enabled();
        await using var port = Create(session, policy);
        var baseline = new CustomPerformanceSettings((CustomPerformanceLevel)cpu, (CustomPerformanceLevel)gpu);
        var target = new CustomPerformanceSettings((CustomPerformanceLevel)targetCpu, (CustomPerformanceLevel)targetGpu);
        Assert.Equal(baseline, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).CustomPerformance);

        await port.SetCustomPerformanceAsync(target, CancellationToken.None);
        Assert.Equal(target, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).CustomPerformance);
        await port.SetCustomPerformanceAsync(baseline, CancellationToken.None);
        Assert.Equal(baseline, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).CustomPerformance);

        Assert.Equal(new[] { $"0101{targetCpu:X2}", $"0102{targetGpu:X2}", $"0101{cpu:X2}", $"0102{gpu:X2}" },
            session.Commands.Where(command => command.CommandId < 0x80)
                .Select(command => Convert.ToHexString(command.Arguments.Span)));
        Assert.All(session.Commands.Where(command => command.CommandId < 0x80),
            command => Assert.Equal(0x07, command.CommandId));
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(2, 1, 2, 2)]
    [InlineData(3, 0, 3, 1)]
    public async Task SourceBackedPairReadbackMismatch_PausesWithoutReplay(
        byte cpu, byte gpu, byte targetCpu, byte targetGpu)
    {
        var session = new Session { Mode = 4, Cpu = cpu, Gpu = gpu, IgnoreLevelWrites = true };
        var policy = Enabled();
        await using var port = Create(session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(
            new((CustomPerformanceLevel)targetCpu, (CustomPerformanceLevel)targetGpu), CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.False(policy.WritesAllowed);
    }
}
