using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

// RZ09-0301, RZ09-0421, RZ09-0423 and RZ09-0427 share one reviewed frame
// contract (evidence/source-reviewed-blades/2026-11-19-rz09-*-keyboard-frames.json).
public sealed partial class SourceReviewedBladeModelTests
{
    private static readonly LightingEffect[] ReviewedMcuFrameEffects =
    [
        LightingEffect.Fire, LightingEffect.Wheel, LightingEffect.Starlight, LightingEffect.Tidal,
        LightingEffect.AmbientAwareness, LightingEffect.AudioMeter,
    ];

    // Breathing Single decodes completely on all four images, including 028A,
    // whose Static hidden palette keeps Static unrestorable.
    private static readonly byte[] McuFrameBaseline = [0, 5, 2, 0, 0, 1, 11, 22, 33, 0, 0, 0];

    public static TheoryData<int> ReviewedMcuFrameProducts => new() { 0x0245, 0x028A, 0x028B, 0x028C };

    public static TheoryData<int, byte[]> UnrestorableMcuFrameBaselines
    {
        get
        {
            var data = new TheoryData<int, byte[]>();
            foreach (int product in new[] { 0x0245, 0x028A, 0x028B, 0x028C })
            {
                data.Add(product, [0, 5, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
                data.Add(product, [0, 5, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
                data.Add(product, [0, 5, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
                data.Add(product, [0, 5, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
                data.Add(product, [0, 5, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_AdmitGridAndUniformEffectsButNoKeyMappedEffects(int product)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(product), true);
        Assert.True(capabilities.SupportsUniformLightingFrames);
        Assert.Null(capabilities.KeyboardLightingLayoutId);
        Assert.All(ReviewedMcuFrameEffects, effect => Assert.Contains(effect, capabilities.LightingEffects));
        foreach (LightingEffect keyMapped in new[] { LightingEffect.Reactive, LightingEffect.Ripple, LightingEffect.Sequence })
        {
            Assert.DoesNotContain(keyMapped, capabilities.LightingEffects);
        }
        // Rule 9: key-triggered effects stay visible as observed-only.
        Assert.Contains(LightingEffect.Reactive, capabilities.ObservedLightingEffects);
        Assert.Contains(LightingEffect.Ripple, capabilities.ObservedLightingEffects);
        Assert.Empty(ExperimentalBladePort.CreateCapabilities(Model(product), false).LightingEffects);

        await using var port = Create(product, new Session());
        Assert.True(port.RequiresLightingRendererOwnership(LightingSettingsFactory.Fire(60)));
        Assert.True(port.RequiresLightingRendererOwnership(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60)));
        Assert.True(port.RequiresLightingRendererOwnership(LightingSettingsFactory.StarlightRandom(60, StarlightLightingDuration.Short)));
        Assert.True(port.RequiresLightingRendererOwnership(
            LightingSettingsFactory.Tidal(new(1, 2, 3), new(4, 5, 6), false, LightingDirection.Leftward, 60)));
        Assert.True(port.RequiresLightingRendererOwnership(LightingSettingsFactory.AudioMeter(1, 60)));
        // Built-in setters keep Breathing colors, both Wave directions and Spectrum.
        Assert.False(port.RequiresLightingRendererOwnership(LightingSettingsFactory.BreathingDual(new(1, 2, 3), new(4, 5, 6), 60)));
        Assert.False(port.RequiresLightingRendererOwnership(LightingSettingsFactory.Wave(LightingDirection.Leftward, 60)));
        Assert.False(port.RequiresLightingRendererOwnership(LightingSettingsFactory.SpectrumCycling(60)));
        Assert.False(port.RequiresLightingRendererOwnership(
            LightingSettingsFactory.Reactive(new(1, 2, 3), 60, ReactiveLightingDuration.Medium)));
    }

    [Fact]
    public void McuFrames_RowEncodingHasKnownAnswerAndRejectsOutOfBoundsRows()
    {
        RgbColor[] colors = Enumerable.Range(0, 96).Select(index => new RgbColor((byte)index, 17, 33)).ToArray();
        byte[] row = ExperimentalBladeLightingFrame.CreateRow(5, colors, padded: false);
        Assert.Equal(52, row.Length);
        Assert.Equal(new byte[] { 0xFF, 5, 0, 15, 80, 17, 33 }, row.Take(7));
        Assert.Equal(new byte[] { 95, 17, 33 }, row.TakeLast(3));
        byte[] report = RazerReport.Create(31, 3, 0x0B, ExperimentalBladeLightingFrame.CreateRow(0, colors, padded: false)).Encode();
        Assert.Equal(Convert.FromHexString("001F00000034030BFF00000F001121"), report.Take(15));
        // The firmware rejects row 6 and beyond; the encoder never forms one.
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentalBladeLightingFrame.CreateRow(6, colors, padded: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentalBladeLightingFrame.CreateRow(-1, colors, padded: false));
        Assert.Throws<ArgumentException>(() => ExperimentalBladeLightingFrame.CreateRow(0, colors[..95], padded: false));
        byte[] activation = RazerReport.Create(31, 15, 2, new byte[] { 0, 5, 8 }).Encode();
        Assert.Equal(Convert.FromHexString("001F000000030F02000508"), activation.Take(11));
    }

    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_JournalRowsActivateConfirmThenRestoreThroughLegacySetter(int product)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { Brightness = 129, McuLighting = McuFrameBaseline.ToArray() };
        bool journalledBeforeFirstWrite = false;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId < 0x80 && !session.Commands.Any(prior => prior.CommandId < 0x80))
            {
                journalledBeforeFirstWrite = storage.Journal.Load().Baseline is not null;
            }
        };
        await using var port = CreateMcuFrames(product, session, ownership: new() { Journal = storage.Journal });

        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);

        Assert.True(journalledBeforeFirstWrite);
        Assert.Equal(new ExperimentalLightingBaseline(
            LightingSettingsFactory.BreathingSingle(new(11, 22, 33), 51), 129), storage.Journal.Load().Baseline);
        int first = session.Commands.FindIndex(command => command.CommandId < 0x80);
        Assert.Equal(new[]
        {
            "03/0B row0", "03/0B row1", "03/0B row2", "03/0B row3", "03/0B row4", "03/0B row5",
            "0F/02 [0,5,8]", "0F/82 region5", "03/03", "03/83",
        }, session.Commands.Skip(first).Select(DescribeMcu).ToArray());
        Assert.All(session.Commands.Where(command => command.ClassId == 3 && command.CommandId == 0x0B), command =>
        {
            Assert.Equal(52, command.Arguments.Length);
            Assert.Equal(new byte[] { 0xFF, command.Arguments.Span[1], 0, 15 }, command.Arguments.Span[..4].ToArray());
            Assert.InRange(command.Arguments.Span[1], (byte)0, (byte)5);
        });
        Assert.True(session.McuCustomOverlay);
        Assert.Equal(LightingRendererOwnershipState.Fire, port.OwnershipState);
        Assert.Null(await port.ReadStateAsync(default));
        int beforeRestore = session.Commands.Count;

        await port.RelinquishRendererAsync(default);

        // The legacy setter copies saved brightness (128); only then is 129 restored.
        Assert.Equal(new[] { "03/83", "0F/82 region5", "03/0A [3,1,11,22,33]", "0F/82 region5", "03/83", "03/03", "03/83" },
            session.Commands.Skip(beforeRestore).Select(DescribeMcu).ToArray());
        Assert.Equal(McuFrameBaseline, session.McuLighting);
        Assert.Equal(129, session.Brightness);
        Assert.False(session.McuCustomOverlay);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
        AssertMcuFrameContract(session);
    }

    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_RestoreWritesBrightnessOnlyWhenTheLegacySetterChangedIt(int product)
    {
        var session = new Session { Brightness = 129, SavedBrightness = 129, McuLighting = McuFrameBaseline.ToArray() };
        await using var port = CreateMcuFrames(product, session);
        await port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 51), default);
        int beforeRestore = session.Commands.Count;

        await port.RelinquishRendererAsync(default);

        Assert.Equal(new[] { "03/83", "0F/82 region5", "03/0A [3,1,11,22,33]", "0F/82 region5", "03/83", "03/83" },
            session.Commands.Skip(beforeRestore).Select(DescribeMcu).ToArray());
        Assert.Equal(129, session.Brightness);
        Assert.Equal(McuFrameBaseline, session.McuLighting);
        AssertMcuFrameContract(session);
    }

    [Theory]
    [MemberData(nameof(UnrestorableMcuFrameBaselines))]
    public async Task McuFrames_UnrestorableBaselineRefusesHostFramesBeforeAnyWrite(int product, byte[] baseline)
    {
        foreach (LightingSettings requested in new[]
        {
            LightingSettingsFactory.Fire(60),
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 60),
        })
        {
            using var storage = new McuFrameJournalFixture(product);
            var session = new Session { McuLighting = baseline.ToArray() };
            await using var port = CreateMcuFrames(product, session, ownership: new() { Journal = storage.Journal });

            BladeWriteException refused = await Assert.ThrowsAsync<BladeWriteException>(() =>
                port.SetLightingAsync(requested, default));

            (Type cause, string message) = baseline[2] switch
            {
                8 => (typeof(CapabilityUnavailableException), "A readable pre-frame lighting baseline is required for host rendering."),
                3 => (typeof(CapabilityUnavailableException), "Host lighting can restore only Off, Static, Breathing or Wave. Choose one of them first."),
                _ => (typeof(DeviceStateContentRejectedException), "The reviewed MCU lighting reply has an unknown effect or inconsistent parameters."),
            };
            Assert.Equal(WriteSubmissionState.KnownNotApplied, refused.SubmissionState);
            Assert.Equal(message, refused.Message);
            Assert.IsType(cause, refused.InnerException);

            Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
            Assert.Equal(baseline, session.McuLighting);
            Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
            Assert.Null(storage.Journal.Load().Baseline);
        }
    }

    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_SpectrumBaselineRefusesEvenWithoutAJournal(int product)
    {
        var session = new Session { McuLighting = [0, 5, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = CreateMcuFrames(product, session);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Tidal(new(1, 2, 3), new(4, 5, 6), false, LightingDirection.Rightward, 60), default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal("Host lighting can restore only Off, Static, Breathing or Wave. Choose one of them first.", failure.Message);
        Assert.IsType<CapabilityUnavailableException>(failure.InnerException);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);

        // Spectrum itself stays a built-in effect, set through the legacy setter.
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), default);
        Assert.Contains(session.Commands, command => DescribeMcu(command) == "03/0A [4]");
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 3 && command.CommandId == 0x0B);
    }

    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_MonitoringReadsSelectionBeforeEveryBatchAndStopsWithoutWrites(int product)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { McuLighting = McuFrameBaseline.ToArray() };
        session.EchoLightingAck = true;
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        var clock = new VaporChamberClock();
        await using var port = new ExperimentalBladePort(session, new(), Model(product), policy, () => true,
            () => PowerSource.PluggedIn, sharedLightingOwnership: new() { Journal = storage.Journal },
            lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await clock.WaitForTimerCountAsync(1);
        int beforeBatch = session.Commands.Count;

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);

        Assert.Equal(new[] { "0F/82 region5", "03/0B row0", "03/0B row1", "03/0B row2", "03/0B row3", "03/0B row4", "03/0B row5" },
            session.Commands.Skip(beforeBatch).Select(DescribeMcu).ToArray());

        // GPIO rising edge: saved records return while the custom flag stays set.
        session.McuLighting = [0, 5, 4, 1, 40, 0, 0, 0, 0, 0, 0, 0];
        int beforeLoss = session.Commands.Count;
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await WaitForOwnershipAsync(port, LightingRendererOwnershipState.Faulted);

        Assert.Equal(new[] { "0F/82 region5" }, session.Commands.Skip(beforeLoss).Select(DescribeMcu).ToArray());
        Assert.True(policy.WritesAllowed);
        Assert.True(session.McuCustomOverlay);
        Assert.NotNull(storage.Journal.Load().Baseline);
        Assert.Equal(LightingRendererOwnershipState.Faulted, await port.RelinquishRendererAsync(default));
        CapabilityUnavailableException blocked = await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));
        Assert.Contains("confirmed built-in", blocked.Message, StringComparison.Ordinal);
        // One fresh read showed an effect other than the baseline; nothing was written.
        Assert.Equal(new[] { "0F/82 region5", "0F/82 region5" }, session.Commands.Skip(beforeLoss).Select(DescribeMcu).ToArray());

        // An explicit built-in choice clears the custom flag and the journal.
        await port.SetLightingAsync(LightingSettingsFactory.BreathingSingle(new(11, 22, 33), 50), default);
        Assert.False(session.McuCustomOverlay);
        Assert.Equal(McuFrameBaseline, session.McuLighting);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
        AssertMcuFrameContract(session);
    }

    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_UniformFramesMonitorEachLaterFrameAndStopOnLostSelection(int product)
    {
        var session = new Session { Brightness = 129, McuLighting = McuFrameBaseline.ToArray() };
        await using var port = CreateMcuFrames(product, session, clock: TimeProvider.System);
        await port.SetLightingAsync(LightingSettingsFactory.AudioMeter(1, 51), default);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 3 && command.CommandId == 0x0B);

        int beforeFirst = session.Commands.Count;
        await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
        Assert.Equal(new[]
        {
            "0F/82 region5",
            "03/0B row0", "03/0B row1", "03/0B row2", "03/0B row3", "03/0B row4", "03/0B row5",
            "0F/02 [0,5,8]", "0F/82 region5",
        }, session.Commands.Skip(beforeFirst).Select(DescribeMcu).ToArray());
        Assert.All(session.Commands.Skip(beforeFirst + 1).Take(6), row =>
            Assert.Equal(Enumerable.Repeat(new byte[] { 9, 8, 7 }, 16).SelectMany(rgb => rgb), row.Arguments.Span[4..].ToArray()));

        int beforeSecond = session.Commands.Count;
        await port.PushUniformLightingFrameAsync(new(new RgbColor(1, 2, 3)), default);
        Assert.Equal("0F/82 region5", DescribeMcu(session.Commands[beforeSecond]));
        Assert.Equal(6, session.Commands.Skip(beforeSecond).Count(command => command.ClassId == 3 && command.CommandId == 0x0B));

        session.McuLighting = [0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        int beforeLoss = session.Commands.Count;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.PushUniformLightingFrameAsync(new(new RgbColor(4, 5, 6)), default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.Equal(new[] { "0F/82 region5" }, session.Commands.Skip(beforeLoss).Select(DescribeMcu).ToArray());
        // The faulted owner no longer accepts samples.
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PushUniformLightingFrameAsync(new(new RgbColor(4, 5, 6)), default));
        Assert.Equal(beforeLoss + 1, session.Commands.Count);
        AssertMcuFrameContract(session);
    }

    [Theory]
    [MemberData(nameof(ReviewedMcuFrameProducts))]
    public async Task McuFrames_ExternallyChangedSelectionIsNeitherRestoredNorReplaced(int product)
    {
        foreach (bool replace in new[] { false, true })
        {
            using var storage = new McuFrameJournalFixture(product);
            var session = new Session { McuLighting = McuFrameBaseline.ToArray() };
            await using var port = CreateMcuFrames(product, session, ownership: new() { Journal = storage.Journal });
            await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
            byte[] external = [0, 5, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0];
            session.McuLighting = external.ToArray();
            int before = session.Commands.Count;

            if (replace)
            {
                await Assert.ThrowsAsync<BladeWriteException>(() =>
                    port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default));
            }
            else
            {
                await Assert.ThrowsAsync<BladeWriteException>(() => port.RelinquishRendererAsync(default));
            }

            Assert.DoesNotContain(session.Commands.Skip(before), command => command.CommandId < 0x80);
            Assert.Equal(external, session.McuLighting);
            Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
            Assert.NotNull(storage.Journal.Load().Baseline);
            AssertMcuFrameContract(session);
        }
    }

    [Theory]
    [InlineData(0x0245, "row")]
    [InlineData(0x028A, "row")]
    [InlineData(0x028B, "activation")]
    [InlineData(0x028C, "activation")]
    [InlineData(0x0245, "confirmation")]
    [InlineData(0x028A, "confirmation")]
    public async Task McuFrames_PartialOrAmbiguousSubmissionFaultsWithoutReplay(int product, string stage)
    {
        using var storage = new McuFrameJournalFixture(product);
        var session = new Session { McuLighting = McuFrameBaseline.ToArray() };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        bool activated = false;
        session.BeforeSubmission = command =>
        {
            if (stage == "row" && command.ClassId == 3 && command.CommandId == 0x0B && command.Arguments.Span[1] == 3)
            {
                throw new IOException("Simulated row uncertainty.");
            }
            if (command.ClassId == 15 && command.CommandId == 2)
            {
                activated = true;
                if (stage == "activation") { throw new IOException("Simulated selection uncertainty."); }
            }
            if (stage == "confirmation" && activated && command.ClassId == 15 && command.CommandId == 0x82)
            {
                throw new IOException("Simulated confirmation failure.");
            }
        };
        await using var port = CreateMcuFrames(product, session, policy, new() { Journal = storage.Journal });

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.NotNull(storage.Journal.Load().Baseline);
        Assert.Equal(stage == "row" ? 3 : 6, session.Commands.Count(command => command.ClassId == 3 && command.CommandId == 0x0B));
        int count = session.Commands.Count;
        // Paused writes refuse the next renderer before any reconciliation read.
        BladeWriteException paused = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, paused.SubmissionState);
        Assert.Equal("Experimental writes are paused after an uncertain result. Check the current settings, then resume experimental controls.",
            paused.Message);
        Assert.Equal(LightingRendererOwnershipState.Faulted, await port.RelinquishRendererAsync(default));
        Assert.Equal(count, session.Commands.Count);
        AssertMcuFrameContract(session);
    }

    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    public async Task McuFrames_LampArrayModelsKeepFramesUnavailable(int product)
    {
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(Model(product), true);
        Assert.False(capabilities.SupportsUniformLightingFrames);
        Assert.Empty(capabilities.LightingEffects.Intersect(ReviewedMcuFrameEffects));
        Assert.Contains(LightingEffect.Fire, capabilities.ObservedLightingEffects);
        ExperimentalFeatureEvidence frames = Assert.Single(new ExperimentalFeaturesPolicy(Model(product)).Snapshot()
            .FeatureEvidence, feature => feature.Feature == "KeyboardCustomFrames");
        Assert.StartsWith("NotEstablished", frames.Basis, StringComparison.Ordinal);

        var session = new Session { EchoLightingAck = true, McuLighting = McuFrameBaseline.ToArray() };
        await using var port = Create(product, session);
        Assert.False(port.RequiresLightingRendererOwnership(LightingSettingsFactory.Fire(60)));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));
        Assert.Empty(session.Commands);
        // Built-ins keep the separately reviewed 0F/02 setters.
        await port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default);
        Assert.Contains(session.Commands, command => command.ClassId == 15 && command.CommandId == 2);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 3 && command.CommandId is 0x0A or 0x0B);
    }

    [Theory]
    [InlineData("rz09-0301", 0x0245, true)]
    [InlineData("rz09-0421", 0x028A, true)]
    [InlineData("rz09-0423", 0x028B, true)]
    [InlineData("rz09-0427", 0x028C, true)]
    [InlineData("rz09-0509", 0x02B8, false)]
    [InlineData("rz09-0510", 0x02B7, false)]
    public void McuFrames_ReviewEvidenceMatchesAdmission(string name, int product, bool admitted)
    {
        string directory = FindEvidenceDirectory();
        string file = Path.Combine(directory, $"2026-11-19-{name}-keyboard-frames.json");
        Assert.Equal($"evidence/source-reviewed-blades/2026-11-19-{name}-keyboard-frames.json",
            ExperimentalBladeModels.ReviewedMcuKeyboardFrameReview(Model(product)));
        byte[] bytes = File.ReadAllBytes(file);
        Assert.Contains("\r\n", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        ExperimentalBladeModel model = Model(product);
        Assert.Equal(model.ModelNumber, root.GetProperty("model").GetString());
        Assert.Equal($"1532:{product:X4}", root.GetProperty("usbIdentity").GetString());
        Assert.Equal("Pending", root.GetProperty("physicalValidation").GetString());
        Assert.Equal(admitted ? "ADMISSIBLE-WITH-LIMITS" : "NOT-ESTABLISHED", root.GetProperty("verdict").GetString());
        Assert.Equal(admitted, ExperimentalBladePort.SupportsReviewedMcuKeyboardFrames(model));

        JsonElement transport = root.GetProperty("keyboardTransport");
        Assert.Equal(ExperimentalBladeLightingFrame.RowCount, transport.GetProperty("rows").GetInt32());
        Assert.Equal(ExperimentalBladeLightingFrame.ColumnCount, transport.GetProperty("columns").GetInt32());
        Assert.Equal("03/0B", transport.GetProperty("rowCommand").GetString());
        Assert.Equal(ExperimentalBladeLightingFrame.RowPayloadLength, transport.GetProperty("rowDataSize").GetInt32());
        Assert.Equal("FF,row,0,15,RGB*16", transport.GetProperty("rowArguments").GetString());
        Assert.Equal("0F/03", transport.GetProperty("alternativeRowCommand").GetString());
        Assert.Equal("0F/02", transport.GetProperty("activationCommand").GetString());
        Assert.Equal(new[] { 0, 5, 8 }, transport.GetProperty("activationArguments").EnumerateArray().Select(value => value.GetInt32()));
        Assert.False(transport.GetProperty("pixelsReadable").GetBoolean());

        // The decoded and Intel HEX image hashes repeat existing repository pins.
        string pins = string.Concat(new[] { "2026-11-13-modern-mcu-lighting.json", "2026-11-13-rz09-0301-lighting-mcu.json" }
            .Select(pin => File.ReadAllText(Path.Combine(directory, pin))));
        JsonElement firmware = root.GetProperty("firmware");
        Assert.Contains(firmware.GetProperty("hexImageSha256").GetString()!, pins, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(firmware.GetProperty("decodedSha256").GetString()!, pins, StringComparison.OrdinalIgnoreCase);
        JsonElement[] windows = root.GetProperty("windows").EnumerateArray().ToArray();
        Assert.Contains(windows, window => window.GetProperty("name").GetString() == "legacyRows03_0B");
        Assert.All(windows, window => Assert.Matches("^[0-9A-F]{64}$", window.GetProperty("sha256").GetString()!));
    }

    private static string FindEvidenceDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        const string relative = "evidence/source-reviewed-blades";
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, relative)))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, relative);
    }

    // The four images never receive 0F/03 or a non-keyboard 0F selection.
    private static void AssertMcuFrameContract(Session session)
    {
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 15 && command.CommandId == 3);
        Assert.All(session.Commands.Where(command => command.ClassId == 15), command =>
            Assert.Equal(5, command.Arguments.Span[1]));
        Assert.All(session.Commands.Where(command => command.ClassId == 15 && command.CommandId == 2), command =>
            Assert.Equal(new byte[] { 0, 5, 8 }, command.Arguments.ToArray()));
        Assert.All(session.Commands.Where(command => command.ClassId == 3 && command.CommandId == 0x0B), command =>
            Assert.InRange(command.Arguments.Span[1], (byte)0, (byte)5));
    }

    private static async Task WaitForOwnershipAsync(ExperimentalBladePort port, LightingRendererOwnershipState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (port.OwnershipState != expected)
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private static ExperimentalBladePort CreateMcuFrames(int product, Session session,
        ExperimentalFeaturesPolicy? policy = null, ExperimentalLightingOwnershipState? ownership = null,
        TimeProvider? clock = null)
    {
        session.EchoLightingAck = true;
        return new(session, new(), Model(product), policy ?? new(Model(product)), () => true,
            () => PowerSource.PluggedIn, sharedLightingOwnership: ownership,
            lightingTimeProvider: clock ?? new FrozenLightingClock());
    }

    private static string DescribeMcu(RazerCommandSpec command) => (command.ClassId, command.CommandId) switch
    {
        (3, 0x0A) => $"03/0A [{string.Join(',', command.Arguments.ToArray())}]",
        _ => Describe(command),
    };

    private sealed class McuFrameJournalFixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("openblade-mcu-frame-journal-");

        internal McuFrameJournalFixture(int product)
        {
            ExperimentalBladeModel model = Model(product);
            Journal = new(directory.FullName, model.ModelNumber, model.ProductId, _ => { });
        }

        internal ExperimentalLightingBaselineJournal Journal { get; }

        public void Dispose() => directory.Delete(recursive: true);
    }
}
