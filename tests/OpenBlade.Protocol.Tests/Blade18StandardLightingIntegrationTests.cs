using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blade18StandardOptions_StreamWithOwnedIntentAndRestoreBaseline(bool wave)
    {
        var clock = new AnimationClock();
        var session = new Session();
        await using var port = AnimatedPort(session, clock);
        LightingSettings settings = wave
            ? LightingSettingsFactory.Wave(LightingDirection.Leftward, 50)
            : LightingSettingsFactory.BreathingDual(new(255, 0, 0), new(0, 0, 255), 50);
        await port.SetLightingAsync(settings, CancellationToken.None);
        Assert.Equal(wave ? LightingRendererOwnershipState.Wave
            : LightingRendererOwnershipState.Breathing, port.OwnershipState);
        Assert.Null(await port.ReadStateAsync(CancellationToken.None));
        await clock.WaitForTimerCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        Assert.Equal(2, session.AnimationBatches);
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, session.AnimationBatches);
    }
}
