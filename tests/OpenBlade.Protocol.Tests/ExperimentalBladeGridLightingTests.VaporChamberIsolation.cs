using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    // LED38 frames are RZ09-0529 only. Drive every other host-frame grid model
    // through spatial, key-free animation and a sampled renderer: none may read or
    // select region 38 or submit a seventh row.
    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x029D)]
    [InlineData(0x029F)]
    public async Task GridFrames_OtherModelsNeverAddressRz090529VaporChamber(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var clock = new AnimationClock();
        var session = new Session
        {
            Raw = 77,
            ExpectedFrameTransactionId = productId == 0x029F ? null : (byte)0xFF,
        };
        await using var port = new ExperimentalBladePort(session, new(), model, new(model),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);

        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        await port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 50),
            CancellationToken.None);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50),
            CancellationToken.None);
        await port.PushUniformLightingFrameAsync(new UniformLightingFrame(new RgbColor(9, 8, 7)), CancellationToken.None);
        await port.RelinquishRendererAsync(CancellationToken.None);

        RazerCommandSpec[] rows = session.Commands
            .Where(command => command.ClassId == 0x03 && command.CommandId == 0x0B).ToArray();
        Assert.True(rows.Length >= 18);
        Assert.All(rows, row => Assert.InRange(row.Arguments.Span[1], 0, 5));
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0x0F
            && command.Arguments.Length > 1 && command.Arguments.Span[1] == 38);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }
}
