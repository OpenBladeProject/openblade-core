using OpenBlade.Contracts;
using OpenBlade.FanStop.Guardian;

namespace OpenBlade.Service.Tests;

public sealed class FanStopContinuationPolicyTests
{
    [Theory]
    [InlineData(2001, false, FanStopEndReason.SafetyCondition)]
    [InlineData(0, true, FanStopEndReason.SafetyCondition)]
    [InlineData(0, false, FanStopEndReason.ThermalThreshold)]
    public async Task SlowOrThrowingThermalExitCannotBecomeFreshThermalRearmEvidence(
        int milliseconds, bool throws, FanStopEndReason expected)
    {
        var clock = new Clock();
        using var sampler = new So6120ServiceSafetySampler(new Source(() =>
        {
            clock.Advance(milliseconds);
            if (throws) { throw new IOException(); }
            return new(81, null, true, true);
        }), FanStopSettings.Default, clock, () => new(true));
        var result = await sampler.WaitForInitialAsync();
        Assert.False(result.Admitted);
        Assert.Equal(expected, result.EndReason);
        Assert.Equal(expected == FanStopEndReason.ThermalThreshold, result.ThermalDecision is not null);
    }

    private sealed class Clock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now;
        internal void Advance(int milliseconds) => now += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    private sealed class Source(Func<FanTemperatureReadings> read) : IFanTemperatureSource
    {
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(read());
    }
}
