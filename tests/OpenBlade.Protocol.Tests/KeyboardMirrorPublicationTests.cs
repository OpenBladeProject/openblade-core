using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

[CollectionDefinition("Keyboard mirror snapshots", DisableParallelization = true)]
public sealed class KeyboardMirrorSnapshotCollection;

public sealed partial class ProductionLightingPortTests
{
    [Collection("Keyboard mirror snapshots")]
    public sealed class KeyboardMirrorPublicationTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CompleteFrame_PublishesAfterTransitionAndClearsOnRelinquish(bool rz090528)
        {
            ResetSnapshot();
            var session = new LightingSession(initialBrightness: 0);
            var writer = new MatrixWriter { WriteObserved = () => Assert.Null(KeyboardLightingFrameStream.Latest) };
            session.BrightnessQueried = () => Assert.Null(KeyboardLightingFrameStream.Latest);
            await using IProductionLightingPort port = CreateOwnedRendererReadPort(
                session, new ManualTimeProvider(), rz090528, writer);

            await port.SetLightingAsync(LightingSettingsFactory.Static(new RgbColor(240, 120, 60), 100), default);

            CoolingPadMirrorFrame observed = Assert.IsType<CoolingPadMirrorFrame>(KeyboardLightingFrameStream.Latest);
            Assert.Equal(18, observed.Colors.Count);
            Assert.Contains(observed.Colors, color => color.R > 0 && color.G > 0 && color.B > 0);
            await port.RelinquishRendererAsync(default);
            Assert.Null(KeyboardLightingFrameStream.Latest);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FrameFollowedByFailedBrightnessReadback_DoesNotPublish(bool rz090528)
        {
            ResetSnapshot();
            var session = new LightingSession(initialBrightness: 0) { FailBrightnessQueryOnCall = 2 };
            await using IProductionLightingPort port = CreateOwnedRendererReadPort(
                session, new ManualTimeProvider(), rz090528);

            await Assert.ThrowsAnyAsync<Exception>(() => port.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(240, 120, 60), 100), default));

            Assert.Null(KeyboardLightingFrameStream.Latest);
        }

        [Fact]
        public async Task OlderRendererRelinquish_PreservesNewOwnersSnapshot()
        {
            ResetSnapshot();
            await using var first = CreatePort(new LightingSession(255), new MatrixWriter(), admitted: true);
            await using var second = CreatePort(new LightingSession(255), new MatrixWriter(), admitted: true);
            await first.SetLightingAsync(LightingSettingsFactory.Static(new RgbColor(240, 0, 0), 100), default);
            await second.SetLightingAsync(LightingSettingsFactory.Static(new RgbColor(0, 240, 0), 100), default);
            CoolingPadMirrorFrame newer = Assert.IsType<CoolingPadMirrorFrame>(KeyboardLightingFrameStream.Latest);

            await first.RelinquishRendererAsync(default);

            Assert.Same(newer, KeyboardLightingFrameStream.Latest);
            await second.RelinquishRendererAsync(default);
            Assert.Null(KeyboardLightingFrameStream.Latest);
        }

        [Fact]
        public async Task PartialSetOnlyFrame_ClearsPriorSnapshotAndDoesNotPublishPartialRows()
        {
            ResetSnapshot();
            var session = new LightingSession(255);
            await using var port = new Rz090528BrightnessLightingPort(
                new ValidationActor(session, new BatteryCommands(), new TemporaryCommands()),
                TimeSpan.FromSeconds(1), allSynapseEffectsAdmitted: true,
                timeProvider: new ManualTimeProvider());
            bool admitted = true;
            port.SetRecoveryWriteAdmission(() => admitted);
            await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 100), default);
            Assert.NotNull(KeyboardLightingFrameStream.Latest);
            session.BeforeStreamSubmission = index => { if (index == 2) { admitted = false; } };
            var colors = new RgbColor[AmbientLightingFrame.SlotCount];

            await Assert.ThrowsAsync<BladeWriteException>(() => port.PushAmbientLightingFrameAsync(
                new AmbientLightingFrame(colors), default));

            Assert.Null(KeyboardLightingFrameStream.Latest);
        }

        private static void ResetSnapshot()
        {
            var owner = new object();
            KeyboardLightingFrameStream.PublishUniform(default, owner);
            KeyboardLightingFrameStream.Clear(owner);
        }
    }
}

public sealed partial class ExperimentalBladeLightingTests
{
    [Collection("Keyboard mirror snapshots")]
    public sealed class KeyboardMirrorPublicationTests
    {
        [Theory]
        [InlineData(0x029F)]
        [InlineData(0x02A0)]
        public async Task UniformRowsOrSetOnlyBatch_PublishCompletedSampleAndClearOnRelease(int productId)
        {
            ResetSnapshot();
            ExperimentalBladeModel model = ModelFor((ushort)productId);
            var session = new Session();
            await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
            await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
            Assert.Null(KeyboardLightingFrameStream.Latest);
            var color = new RgbColor(120, 60, 30);

            await port.PushUniformLightingFrameAsync(new UniformLightingFrame(color), default);

            CoolingPadMirrorFrame observed = Assert.IsType<CoolingPadMirrorFrame>(KeyboardLightingFrameStream.Latest);
            Assert.All(observed.Colors, actual => Assert.Equal(new CoolingPadColor(120, 60, 30), actual));
            await port.RelinquishRendererAsync(default);
            Assert.Null(KeyboardLightingFrameStream.Latest);
        }

        [Theory]
        [InlineData(0x029F)]
        [InlineData(0x02A0)]
        public async Task FailedUniformRowsOrBatch_NeverPublishAndClearPreviousFrame(int productId)
        {
            ResetSnapshot();
            ExperimentalBladeModel model = ModelFor((ushort)productId);
            var session = new Session();
            await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
            await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
            await port.PushUniformLightingFrameAsync(new UniformLightingFrame(new RgbColor(120, 60, 30)), default);
            Assert.NotNull(KeyboardLightingFrameStream.Latest);
            session.FailFrameRow = true;
            session.FailAnimationBatch = session.AnimationBatches + 1;

            await Assert.ThrowsAsync<BladeWriteException>(() => port.PushUniformLightingFrameAsync(
                new UniformLightingFrame(new RgbColor(10, 20, 30)), default));

            Assert.Null(KeyboardLightingFrameStream.Latest);
        }

        [Theory]
        [InlineData(0x029F)]
        [InlineData(0x02A0)]
        public async Task StaticHostRows_PublishOnlyAfterFullTransition(int productId)
        {
            ResetSnapshot();
            ExperimentalBladeModel model = ModelFor((ushort)productId);
            var session = new Session();
            session.BeforeSubmission = _ => Assert.Null(KeyboardLightingFrameStream.Latest);
            await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

            await port.SetLightingAsync(LightingSettingsFactory.Static(new RgbColor(90, 60, 30), 50), default);

            CoolingPadMirrorFrame observed = Assert.IsType<CoolingPadMirrorFrame>(KeyboardLightingFrameStream.Latest);
            Assert.All(observed.Colors, actual => Assert.Equal(new CoolingPadColor(90, 60, 30), actual));
            session.BeforeSubmission = null;
        }

        [Theory]
        [InlineData(0x029F)]
        [InlineData(0x02A0)]
        public async Task DirectDisposal_ClearsTheRetiredOwnersSnapshot(int productId)
        {
            ResetSnapshot();
            ExperimentalBladeModel model = ModelFor((ushort)productId);
            var session = new Session();
            var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
            await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
            await port.PushUniformLightingFrameAsync(new UniformLightingFrame(new RgbColor(120, 60, 30)), default);
            Assert.NotNull(KeyboardLightingFrameStream.Latest);

            await port.DisposeAsync();

            Assert.Null(KeyboardLightingFrameStream.Latest);
        }

        private static void ResetSnapshot()
        {
            var owner = new object();
            KeyboardLightingFrameStream.PublishUniform(default, owner);
            KeyboardLightingFrameStream.Clear(owner);
        }
    }
}
