using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(0, "map-afe3")]
    [InlineData(1, "map-afe3")]
    [InlineData(5, "map-afe3")]
    [InlineData(8, "map-afe3")]
    [InlineData(9, "map-afe3")]
    [InlineData(11, "map-afe3")]
    [InlineData(12, "map-b63b")]
    [InlineData(2, "map-b313")]
    [InlineData(3, "map-b313")]
    [InlineData(4, "map-b313")]
    [InlineData(6, "map-b313")]
    [InlineData(7, "map-b313")]
    [InlineData(10, "map-b313")]
    [InlineData(13, "map-b313")]
    [InlineData(255, "map-b313")]
    public async Task Blade14Edition_ReadsExactTwoByteGetterWithoutAnySetter(byte edition, string mapId)
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var session = new Session { EditionReply = [edition, 77] };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        await port.InitializeBlade14KeyboardEditionAsync(CancellationToken.None);

        Assert.Equal(edition, port.Capabilities.Blade14KeyboardEdition);
        Assert.Equal(KeyboardLightingLayoutIds.Rz09_0530, port.Capabilities.KeyboardLightingLayoutId);
        Assert.Null(port.Capabilities.Blade18KeyboardLayoutId);
        RazerCommandSpec query = Assert.Single(session.Commands);
        Assert.Equal((byte)0, query.ClassId);
        Assert.Equal((byte)0x86, query.CommandId);
        Assert.Equal(new byte[] { 0, 0 }, query.Arguments.ToArray());
        Assert.Equal(2, query.ExpectedPayloadLength);
        Assert.NotNull(query.PayloadValidator);
        Assert.Null(query.SubmissionAllowed);
        Assert.True(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Contains(policy.Snapshot().Observations, observation =>
            observation.Operation == "KeyboardLayout" && observation.Outcome == "Confirmed");
        Assert.Equal(mapId, Blade14KeyboardLightingLayout.GetMapId(edition));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    public async Task Blade14Edition_MalformedOrUnavailableReadKeepsCommonMapAndControls(int length, bool fail)
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        byte[] reply = new byte[length];
        reply[0] = 12;
        var session = new Session { EditionReply = reply, FailEditionRead = fail };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        await port.InitializeBlade14KeyboardEditionAsync(CancellationToken.None);

        Assert.Null(port.Capabilities.Blade14KeyboardEdition);
        RazerCommandSpec query = Assert.Single(session.Commands);
        Assert.Equal((byte)0x86, query.CommandId);
        Assert.True(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task Blade14Edition_DoesNotSwallowCancellationAsUnavailableMetadata()
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        using var cancellation = new CancellationTokenSource();
        var session = new Session { CancelEditionRead = cancellation };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            port.InitializeBlade14KeyboardEditionAsync(cancellation.Token));

        Assert.Null(port.Capabilities.Blade14KeyboardEdition);
    }

    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02C7)]
    [InlineData(0x029F)]
    public async Task Blade14Edition_DoesNotProbeAnotherModel(int productId)
    {
        var session = new Session();
        ExperimentalBladeModel other = ModelFor((ushort)productId);
        await using var port = Create(session, new ExperimentalFeaturesPolicy(other), other);

        await port.InitializeBlade14KeyboardEditionAsync(CancellationToken.None);

        Assert.Empty(session.Commands);
        Assert.Null(port.Capabilities.Blade14KeyboardEdition);
    }

    [Theory]
    [InlineData(12, 30)] // International3 exists only in map B63B.
    [InlineData(2, 66)] // Non-US backslash exists only in map B313.
    [InlineData(0, 47)] // HID 0x31 sits at this slot only in map AFE3.
    public async Task Blade14ConfirmedEdition_AdmitsRegionalPulseWithoutWritingOnInput(byte edition, int slot)
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var session = new Session { EditionReply = [edition, 0], ExpectedFrameTransactionId = 0xFF };
        var clock = new AnimationClock();
        await using var port = new ExperimentalBladePort(session, new(), model, new(model),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        await port.InitializeBlade14KeyboardEditionAsync(CancellationToken.None);
        await port.SetLightingAsync(LightingSettingsFactory.Ripple(new RgbColor(0, 200, 40), 50),
            CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        Assert.False(Blade14KeyboardLightingLayout.IsKeySlot(slot));
        Assert.True(Blade14KeyboardLightingLayout.IsKeySlot(edition, slot));
        int before = session.Commands.Count;

        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse((byte)slot), CancellationToken.None);

        Assert.Equal(before, session.Commands.Count);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        RgbColor[] frame = DecodeGridRows(session.Commands.Where(IsFrameRow).TakeLast(6).ToArray());
        Assert.NotEqual(default, frame[slot]);
        await port.RelinquishRendererAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Blade14UnconfirmedEdition_RejectsRegionalPulse()
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var session = new Session { FailEditionRead = true, ExpectedFrameTransactionId = 0xFF };
        var clock = new AnimationClock();
        await using var port = new ExperimentalBladePort(session, new(), model, new(model),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        await port.InitializeBlade14KeyboardEditionAsync(CancellationToken.None);
        await port.SetLightingAsync(LightingSettingsFactory.Reactive(new RgbColor(0, 200, 40), 50,
            ReactiveLightingDuration.Short), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);

        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(30), CancellationToken.None));
        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(50), CancellationToken.None);
        await port.RelinquishRendererAsync(CancellationToken.None);
    }

    [Fact]
    public void Blade14PulsePattern_UsesTheSelectedTableForRippleGeometry()
    {
        var color = new RgbColor(255, 30, 10);
        var common = new ExperimentalGridLightingEffect(LightingSettingsFactory.Ripple(color, 50));
        var mapB63b = new ExperimentalGridLightingEffect(LightingSettingsFactory.Ripple(color, 50),
            blade14Edition: 12);

        Assert.Throws<CapabilityUnavailableException>(() => common.Pulse(30, TimeSpan.Zero));
        mapB63b.Pulse(30, TimeSpan.Zero);
        Assert.Equal(color, mapB63b.RenderFrame(TimeSpan.Zero)[30]);
        Assert.Throws<CapabilityUnavailableException>(() => mapB63b.Pulse(66, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new ExperimentalGridLightingEffect(
            LightingSettingsFactory.SpectrumCycling(50), rz090529: true, blade14Edition: 12));
    }

    [Fact]
    public async Task Blade14Sequence_AcceptsRegionalSlotsInTheThreeTableUnion()
    {
        ExperimentalBladeModel model = ModelFor(0x02C5);
        var clock = new AnimationClock();
        var session = new Session { ExpectedFrameTransactionId = 0xFF };
        await using var port = new ExperimentalBladePort(session, new(), model, new(model),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        var frame = new RgbColor[AmbientLightingFrame.SlotCount];
        frame[30] = new(1, 2, 3);
        frame[66] = new(4, 5, 6);
        string encoded = KeyboardLightingSequenceCodec.Encode([new(200, frame)], KeyboardLightingLayoutIds.Rz09_0530);

        await port.SetLightingAsync(LightingSettingsFactory.Sequence(
            KeyboardLightingLayoutIds.Rz09_0530, encoded, loop: false, brightness: 50), CancellationToken.None);
        await WaitUntilAsync(() => session.Commands.Count(IsFrameRow) == 6);

        Assert.Equal(frame.Take(96), DecodeGridRows(session.Commands.Where(IsFrameRow).ToArray()));
        await port.RelinquishRendererAsync(CancellationToken.None);
    }
}
