using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    // The mirror snapshot is process-wide; share its existing exclusive collection.
    [Collection("Keyboard mirror snapshots")]
    public sealed class ExperimentalLightingCadenceTests
    {
        [Theory]
        [InlineData(0x029F)]
        [InlineData(0x029D)]
        [InlineData(0x02C5)]
        [InlineData(0x02A0)]
        public async Task UniformFrames_ShareSoftwareBudgetAndCanceledWaitPreservesOwnership(int productId)
        {
            ExperimentalBladeModel model = ModelFor((ushort)productId);
            var session = new Session();
            var clock = new AnimationClock();
            var policy = new ExperimentalFeaturesPolicy(model);
            await using var port = new ExperimentalBladePort(session, new(), model, policy,
                () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
            await port.SetLightingAsync(
                LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
            var frame = new UniformLightingFrame(new RgbColor(10, 20, 30));
            await port.PushUniformLightingFrameAsync(frame, default);
            Assert.Equal(6, session.Commands.Count(IsFrameRow));

            Task next = port.PushUniformLightingFrameAsync(frame, default);
            await clock.WaitForTimerCountAsync(1);
            clock.Advance(TimeSpan.FromMilliseconds(49));
            Assert.False(next.IsCompleted);
            Assert.Equal(6, session.Commands.Count(IsFrameRow));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await next;
            Assert.Equal(12, session.Commands.Count(IsFrameRow));

            using var cancellation = new CancellationTokenSource();
            Task canceled = port.PushUniformLightingFrameAsync(frame, cancellation.Token);
            await clock.WaitForTimerCountAsync(2);
            clock.Advance(TimeSpan.FromMilliseconds(25));
            cancellation.Cancel();
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => canceled);
            Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
            Assert.Equal(12, session.Commands.Count(IsFrameRow));
            Assert.True(policy.WritesAllowed);
            Assert.Equal(LightingRendererOwnershipState.AmbientAwareness, port.OwnershipState);

            Task afterCancellation = port.PushUniformLightingFrameAsync(frame, default);
            await clock.WaitForTimerCountAsync(3);
            clock.Advance(TimeSpan.FromMilliseconds(24));
            Assert.False(afterCancellation.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await afterCancellation;
            Assert.Equal(18, session.Commands.Count(IsFrameRow));

            // A new owner can submit immediately and must reacquire activation.
            await port.SetLightingAsync(LightingSettingsFactory.AudioMeter(1, 50), default);
            Task firstForNewOwner = port.PushUniformLightingFrameAsync(frame, default);
            await firstForNewOwner.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(24, session.Commands.Count(IsFrameRow));
            Assert.Equal(productId == 0x02A0 ? 2 : 4,
                session.Commands.Count(IsFrameActivation));
            Assert.Equal(productId == 0x02A0 ? 4 : 0, session.AnimationBatches);
        }

        [Theory]
        [InlineData(0x02C5, LightingEffect.Reactive)]
        [InlineData(0x02C5, LightingEffect.Ripple)]
        [InlineData(0x02A0, LightingEffect.Reactive)]
        [InlineData(0x02A0, LightingEffect.Ripple)]
        public async Task KeyTriggeredFrames_SuppressIdlePreserveKeepaliveAndPublishFinalBlack(
            int productId, LightingEffect effect)
        {
            ExperimentalBladeModel model = ModelFor((ushort)productId);
            var session = new Session();
            var clock = new AnimationClock();
            await using var port = new ExperimentalBladePort(session, new(), model, new(model),
                () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
            await port.SetLightingAsync(CadenceKeySetting(effect), default);
            await clock.WaitForTimerCountAsync(1);
            CoolingPadMirrorFrame? initial = KeyboardLightingFrameStream.Latest;
            Assert.NotNull(initial);
            Assert.Equal(6, session.Commands.Count(IsFrameRow));

            clock.Advance(TimeSpan.FromMilliseconds(50));
            await clock.WaitForTimerCountAsync(2);
            Assert.Equal(6, session.Commands.Count(IsFrameRow));
            Assert.Same(initial, KeyboardLightingFrameStream.Latest);
            clock.Advance(TimeSpan.FromMilliseconds(4950));
            await clock.WaitForTimerCountAsync(3);
            Assert.Equal(12, session.Commands.Count(IsFrameRow));

            await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(CadenceKeySlot(productId)), default);
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await clock.WaitForTimerCountAsync(4);
            Assert.Equal(18, session.Commands.Count(IsFrameRow));
            Assert.Contains(session.Commands.Where(IsFrameRow).TakeLast(6),
                row => row.Arguments.Span[4..].ContainsAnyExcept((byte)0));

            clock.Advance(TimeSpan.FromSeconds(2));
            await clock.WaitForTimerCountAsync(5);
            Assert.Equal(24, session.Commands.Count(IsFrameRow));
            Assert.All(session.Commands.Where(IsFrameRow).TakeLast(6),
                row => Assert.False(row.Arguments.Span[4..].ContainsAnyExcept((byte)0)));
            CoolingPadMirrorFrame? finalBlack = KeyboardLightingFrameStream.Latest;
            Assert.NotNull(finalBlack);
            Assert.NotSame(initial, finalBlack);
            Assert.All(finalBlack.Colors, color => Assert.Equal(new CoolingPadColor(0, 0, 0), color));

            clock.Advance(TimeSpan.FromMilliseconds(50));
            await clock.WaitForTimerCountAsync(6);
            Assert.Equal(24, session.Commands.Count(IsFrameRow));
            Assert.Same(finalBlack, KeyboardLightingFrameStream.Latest);
            clock.Advance(TimeSpan.FromSeconds(5));
            await clock.WaitForTimerCountAsync(7);
            Assert.Equal(30, session.Commands.Count(IsFrameRow));
            Assert.Single(session.Commands, IsFrameActivation);
            await port.RelinquishRendererAsync(default);
            Assert.Null(KeyboardLightingFrameStream.Latest);
        }

        [Theory]
        [InlineData(0x02C5)]
        [InlineData(0x02A0)]
        public async Task KeyTriggeredKeepalive_DeferralDoesNotAdvanceSuccessfulFrameTime(int productId)
        {
            ExperimentalBladeModel model = ModelFor((ushort)productId);
            var session = new Session();
            var clock = new AnimationClock();
            var policy = new ExperimentalFeaturesPolicy(model);
            bool admitted = true;
            await using var port = new ExperimentalBladePort(session, new(), model, policy,
                () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
            port.SetRecoveryWriteAdmission(() => admitted);
            await port.SetLightingAsync(CadenceKeySetting(LightingEffect.Reactive), default);
            await clock.WaitForTimerCountAsync(1);
            CoolingPadMirrorFrame? initial = KeyboardLightingFrameStream.Latest;
            admitted = false;
            clock.Advance(TimeSpan.FromSeconds(5));
            await clock.WaitForTimerCountAsync(2);
            Assert.Equal(6, session.Commands.Count(IsFrameRow));
            Assert.Same(initial, KeyboardLightingFrameStream.Latest);
            Assert.True(policy.WritesAllowed);
            Assert.Equal(LightingRendererOwnershipState.Reactive, port.OwnershipState);

            admitted = true;
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await clock.WaitForTimerCountAsync(3);
            Assert.Equal(12, session.Commands.Count(IsFrameRow));
            clock.Advance(TimeSpan.FromMilliseconds(4950));
            await clock.WaitForTimerCountAsync(4);
            Assert.Equal(12, session.Commands.Count(IsFrameRow));
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await clock.WaitForTimerCountAsync(5);
            Assert.Equal(18, session.Commands.Count(IsFrameRow));
        }

        [Theory]
        [InlineData(0x02C5)]
        [InlineData(0x02A0)]
        public async Task KeyTriggeredFrame_AmbiguousSubmissionClearsMirrorAndNeverReplays(int productId)
        {
            ExperimentalBladeModel model = ModelFor((ushort)productId);
            var session = new Session();
            var clock = new AnimationClock();
            var policy = new ExperimentalFeaturesPolicy(model);
            await using var port = new ExperimentalBladePort(session, new(), model, policy,
                () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
            await port.SetLightingAsync(CadenceKeySetting(LightingEffect.Reactive), default);
            await clock.WaitForTimerCountAsync(1);
            Assert.NotNull(KeyboardLightingFrameStream.Latest);
            session.FailFrameRow = true;
            session.FailAnimationBatch = 2;
            await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(CadenceKeySlot(productId)), default);
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await WaitUntilAsync(() => port.OwnershipState == LightingRendererOwnershipState.Faulted);
            Assert.False(policy.WritesAllowed);
            Assert.Null(KeyboardLightingFrameStream.Latest);
            int commands = session.Commands.Count;
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(commands, session.Commands.Count);
        }

        private static LightingSettings CadenceKeySetting(LightingEffect effect) =>
            effect == LightingEffect.Reactive
                ? LightingSettingsFactory.Reactive(new RgbColor(255, 20, 0), 50, ReactiveLightingDuration.Short)
                : LightingSettingsFactory.Ripple(new RgbColor(255, 20, 0), 50);

        private static byte CadenceKeySlot(int productId)
        {
            bool mapped = productId == 0x02C5
                ? Blade14KeyboardLightingLayout.TryMapScanCode(0x1E, false, out byte slot)
                : Blade18KeyboardLightingLayout.TryMapScanCode(0x10, false, out slot);
            Assert.True(mapped);
            return slot;
        }
    }
}
