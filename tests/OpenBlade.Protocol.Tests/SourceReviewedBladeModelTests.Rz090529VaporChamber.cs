using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    private static readonly byte[] Rz090529StaticKeyboard = [0, 5, 1, 0, 0, 1, 11, 22, 33, 0, 0, 0];
    private static readonly byte[] Rz090529StaticVaporChamber = [0, 38, 1, 0, 0, 1, 1, 2, 3, 0, 0, 0];
    private static readonly byte[] Rz090529WaveVaporChamber = [0, 38, 4, 1, 40, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] Rz090529SpectrumVaporChamber = [0, 38, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    // Each reply is paired with the KI8110 setter written by hand from the reviewed
    // 0F/02 layout, not derived from the production encoder.
    public static TheoryData<byte[], byte[]> Rz090529RestorableVaporChamberBaselines => new()
    {
        { new byte[] { 0, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 0, 38, 0 } },
        { new byte[] { 0, 38, 1, 0, 0, 1, 1, 2, 3, 0, 0, 0 }, new byte[] { 0, 38, 1, 0, 0, 1, 1, 2, 3 } },
        { new byte[] { 0, 38, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 0, 38, 2, 0, 0, 0 } },
        { new byte[] { 0, 38, 2, 0, 0, 1, 1, 2, 3, 0, 0, 0 }, new byte[] { 0, 38, 2, 0, 0, 1, 1, 2, 3 } },
        { new byte[] { 0, 38, 2, 0, 0, 2, 1, 2, 3, 4, 5, 6 }, new byte[] { 0, 38, 2, 0, 0, 2, 1, 2, 3, 4, 5, 6 } },
        { new byte[] { 0, 38, 4, 1, 40, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 0, 38, 4, 1 } },
        { new byte[] { 0, 38, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 0, 38, 4, 2 } },
    };

    [Theory]
    [MemberData(nameof(Rz090529RestorableVaporChamberBaselines))]
    public async Task Rz090529VaporChamber_SelectionIsJournaledFirstAndRestoreWritesReviewedSetter(
        byte[] vaporChamber, byte[] setter)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            Brightness = 129,
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = vaporChamber.ToArray(),
        };
        LightingSettings? recordAtSelection = null;
        string? journalAtSelection = null;
        session.BeforeSubmission = command =>
        {
            if (IsVaporChamberSelection(command))
            {
                journalAtSelection = File.ReadAllText(storage.JournalPath);
                recordAtSelection = ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber;
            }
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });

        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);

        Assert.Equal(8, session.McuVaporChamber[2]);
        Assert.Single(session.Commands, command => IsRz090529Row(command, 6));
        Assert.NotNull(recordAtSelection);
        Assert.Contains("\"schemaVersion\":2", journalAtSelection, StringComparison.Ordinal);

        await port.RelinquishRendererAsync(default);

        RazerCommandSpec restore = Assert.Single(session.Commands, command =>
            IsVaporChamberSet(command) && !IsVaporChamberSelection(command));
        Assert.Equal(setter, restore.Arguments.ToArray());
        Assert.Equal(vaporChamber, session.McuVaporChamber);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.Equal(129, session.Brightness);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_FrameAddsRowSixAndSelectsRegionAfterKeyboard()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session);
        LightingSettings fire = LightingSettingsFactory.Fire(60);

        await port.SetLightingAsync(fire, default);

        int first = session.Commands.FindIndex(command => IsRz090529Row(command, 0));
        Assert.Contains(session.Commands.Take(first), IsVaporChamberRead);
        string[] sequence = session.Commands.Skip(first).Select(Describe).ToArray();
        Assert.Equal(new[]
        {
            "03/0B row0", "03/0B row1", "03/0B row2", "03/0B row3", "03/0B row4", "03/0B row5",
            "03/0B row6", "0F/02 [0,5,8]", "0F/82 region5", "0F/02 [0,38,8]", "0F/82 region38",
        }, sequence);
        byte[] row = session.Commands.Single(command => IsRz090529Row(command, 6)).Arguments.ToArray();
        Assert.Equal(61, row.Length);
        Assert.Equal(new byte[] { 0xFF, 6, 0, 18, 0, 0, 0 }, row.Take(7));
        IReadOnlyList<RgbColor> expected = new ExperimentalGridLightingEffect(fire, rz090529: true)
            .RenderVaporChamber(TimeSpan.Zero);
        for (int column = 0; column < 18; column++)
        {
            RgbColor color = expected[column];
            Assert.Equal(new[] { color.Red, color.Green, color.Blue }, row.Skip(7 + column * 3).Take(3));
        }
    }

    [Fact]
    public void Rz090529VaporChamber_RowEncodingLeavesTransportColumnZeroBlack()
    {
        RgbColor[] colors = Enumerable.Range(0, 18).Select(index => new RgbColor((byte)(index + 1), 50, 60)).ToArray();
        byte[] row = Rz090529KeyboardLightingFrame.EncodeVaporChamberRow(colors);
        Assert.Equal(Rz090529KeyboardLightingFrame.PayloadLength, row.Length);
        Assert.Equal(new byte[] { 0xFF, 6, 0, 18, 0, 0, 0, 1, 50, 60 }, row.Take(10));
        Assert.Equal(new byte[] { 18, 50, 60 }, row.TakeLast(3));
        Assert.Throws<ArgumentException>(() => Rz090529KeyboardLightingFrame.EncodeVaporChamberRow(colors[..17]));
        byte[] activation = RazerReport.Create(31, 15, 2, new byte[] { 0, 38, 8 }).Encode();
        Assert.Equal(Convert.FromHexString("001F000000030F02002608"), activation.Take(11));
    }

    [Fact]
    public void Rz090529VaporChamber_ColorScaleFollowsKeyboardBrightnessPercent()
    {
        var color = new RgbColor(200, 101, 1);
        Assert.Equal(color, Rz090529KeyboardLightingFrame.ScaleVaporChamberColor(color, 100));
        Assert.Equal(new RgbColor(100, 51, 1), Rz090529KeyboardLightingFrame.ScaleVaporChamberColor(color, 50));
        Assert.Equal(new RgbColor(120, 61, 1), Rz090529KeyboardLightingFrame.ScaleVaporChamberColor(color, 60));
        Assert.Equal(default, Rz090529KeyboardLightingFrame.ScaleVaporChamberColor(color, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rz090529KeyboardLightingFrame.ScaleVaporChamberColor(color, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rz090529KeyboardLightingFrame.ScaleVaporChamberColor(color, 101));
    }

    [Theory]
    [InlineData(new byte[] { 0, 38, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 7, 1, 0, 1, 1, 2, 3, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 1, 0, 0, 2, 1, 2, 3, 4, 5, 6 })]
    [InlineData(new byte[] { 0, 38, 4, 1, 39, 0, 0, 0, 0, 0, 0, 0 })]
    public async Task Rz090529VaporChamber_UnrestorableBaselineKeepsFramesKeyboardOnly(byte[] vaporChamber)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = vaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });

        await port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default);
        Assert.Null(storage.Journal.Load().Baseline!.VaporChamber);
        Assert.Contains("\"schemaVersion\":1", File.ReadAllText(storage.JournalPath), StringComparison.Ordinal);
        Assert.DoesNotContain("vaporChamber", File.ReadAllText(storage.JournalPath), StringComparison.OrdinalIgnoreCase);
        await port.RelinquishRendererAsync(default);

        Assert.DoesNotContain(session.Commands, command => IsRz090529Row(command, 6));
        Assert.DoesNotContain(session.Commands, IsVaporChamberSet);
        Assert.Equal(vaporChamber, session.McuVaporChamber);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
    }

    [Theory]
    [InlineData(new byte[] { 0, 5, 1, 0, 0, 1, 1, 2, 3, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(null)]
    public async Task Rz090529VaporChamber_UnreadableRegionLeavesFramesKeyboardOnlyAndIsNotReread(byte[]? reply)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = reply ?? Rz090529StaticVaporChamber.ToArray(),
            RejectVaporChamberEffectQuery = reply is null,
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = CreateRz090529Frames(session, policy, new() { Journal = storage.Journal });

        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        Assert.Equal(LightingRendererOwnershipState.Fire, port.OwnershipState);
        Assert.Contains("\"schemaVersion\":1", File.ReadAllText(storage.JournalPath), StringComparison.Ordinal);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
        await port.RelinquishRendererAsync(default);

        Assert.Single(session.Commands, IsVaporChamberRead);
        Assert.DoesNotContain(session.Commands, command => IsRz090529Row(command, 6));
        Assert.DoesNotContain(session.Commands, IsVaporChamberSet);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
        Assert.True(policy.WritesAllowed);
        Assert.Contains(policy.Snapshot().Observations, item => item.Operation == "VaporChamberEffect"
            && item.FailureCategory == "VaporChamberFramesUnavailable");
    }

    // Every keyboard effect shipped before LED38 frames keeps working, keyboard-only,
    // when the firmware rejects the LED38 effect GET.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task Rz090529VaporChamber_RejectedRegionReadKeepsShippedEffectsWorking(int kind)
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
            RejectVaporChamberEffectQuery = true,
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = CreateRz090529Frames(session, policy);
        LightingSettings requested = kind switch
        {
            0 => LightingSettingsFactory.Fire(60),
            1 => LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60),
            2 => LightingSettingsFactory.Tidal(new(11, 22, 33), new(44, 55, 66), false, LightingDirection.Leftward, 60),
            3 => LightingSettingsFactory.StarlightRandom(60, StarlightLightingDuration.Short),
            4 => LightingSettingsFactory.Ripple(new(200, 100, 50), 60),
            5 => LightingSettingsFactory.Reactive(new(1, 2, 3), 60, ReactiveLightingDuration.Medium),
            6 => Rz090529Sequence(),
            7 => LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50),
            8 => LightingSettingsFactory.AudioMeter(1, 50),
            9 => LightingSettingsFactory.Static(new(7, 8, 9), 50),
            10 => LightingSettingsFactory.Wave(LightingDirection.Rightward, 50),
            _ => LightingSettingsFactory.Off(),
        };

        await port.SetLightingAsync(requested, default);
        if (requested.Effect is LightingEffect.AmbientAwareness or LightingEffect.AudioMeter)
        {
            await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
        }
        await port.RelinquishRendererAsync(default);

        Assert.True(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.DoesNotContain(session.Commands, command => IsRz090529Row(command, 6));
        Assert.DoesNotContain(session.Commands, IsVaporChamberSet);
        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        Assert.InRange(session.Commands.Count(IsVaporChamberRead), 0, 1);
        if (kind is 5 or 6 or >= 9)
        {
            // Reactive, Sequence and built-in effects never address LED38.
            Assert.DoesNotContain(session.Commands, IsVaporChamberRead);
        }
        if (kind < 9)
        {
            Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        }
    }

    [Fact]
    public async Task Rz090529VaporChamber_PoisonedRegionReadFailsBeforeWritesAndIsNotProbedAgain()
    {
        var availability = new ExperimentalVaporChamberAvailability();
        var host = new HostIdentityEvidence("Razer", "Razer Blade 18", null, "test-firmware");
        var hid = new HidHandleIdentity(0x1532, 0x02C7, 1, 2, 91);
        var poisoned = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
            RejectVaporChamberEffectQuery = true,
            RetireOnQueryFailure = true,
        };
        await using (var port = CreateRz090529Frames(poisoned))
        {
            port.BindVaporChamberAvailability(availability, new(host, hid));
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
                port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));
            Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
            Assert.DoesNotContain(poisoned.Commands, command => command.CommandId < 0x80);
        }
        Assert.False(availability.Get(new(host, hid)));

        var fresh = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        await using (var port = CreateRz090529Frames(fresh))
        {
            port.BindVaporChamberAvailability(availability, new(host, hid));
            await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
            await port.RelinquishRendererAsync(default);
        }
        Assert.DoesNotContain(fresh.Commands, IsVaporChamberRead);
        Assert.DoesNotContain(fresh.Commands, command => IsRz090529Row(command, 6));
        Assert.Equal(Rz090529StaticKeyboard, fresh.McuLighting);

        // A different exact HID identity probes again.
        var changed = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        await using (var port = CreateRz090529Frames(changed))
        {
            port.BindVaporChamberAvailability(availability, new(host, hid with { VersionNumber = 92 }));
            await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
            await port.RelinquishRendererAsync(default);
        }
        Assert.Contains(changed.Commands, command => IsRz090529Row(command, 6));
        Assert.Equal(Rz090529StaticVaporChamber, changed.McuVaporChamber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529VaporChamber_KeyboardOnlyEffectsNeverReadOrJournalRegion(bool sequence)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });

        await port.SetLightingAsync(sequence ? Rz090529Sequence()
            : LightingSettingsFactory.Reactive(new(1, 2, 3), 60, ReactiveLightingDuration.Medium), default);
        Assert.Contains("\"schemaVersion\":1", File.ReadAllText(storage.JournalPath), StringComparison.Ordinal);
        // Another controller selects its own LED38 custom frame; OpenBlade never selected LED38.
        session.McuVaporChamber = [0, 38, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        await port.RelinquishRendererAsync(default);

        Assert.DoesNotContain(session.Commands, IsVaporChamberRead);
        Assert.DoesNotContain(session.Commands, IsVaporChamberSet);
        Assert.Equal(8, session.McuVaporChamber[2]);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_UnselectedOwnerLeavesForeignCustomModeOnRelease()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529SpectrumVaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        session.McuVaporChamber = [0, 38, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        await port.RelinquishRendererAsync(default);

        Assert.DoesNotContain(session.Commands, IsVaporChamberSet);
        Assert.Equal(8, session.McuVaporChamber[2]);
    }

    // The sampled renderers choose LED38 in SetLightingAsync but select it only
    // with their first sample, so that sample re-reads LED38 under the same gate hold.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Rz090529VaporChamber_SampledEffectRereadsRegionBeforeFirstSelection(bool audio, int change)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });
        await port.SetLightingAsync(audio ? LightingSettingsFactory.AudioMeter(1, 50)
            : LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        Assert.DoesNotContain(session.Commands, IsVaporChamberSet);
        Assert.Null(storage.Journal.Load().Baseline!.VaporChamber);

        byte[] external = change == 1 ? Rz090529WaveVaporChamber : Rz090529SpectrumVaporChamber;
        if (change != 0)
        {
            session.McuVaporChamber = external.ToArray();
        }
        LightingSettings? recordAtSelection = null;
        session.BeforeSubmission = command =>
        {
            if (IsVaporChamberSelection(command))
            {
                recordAtSelection = ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber;
            }
        };
        int before = session.Commands.Count;
        await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
        RazerCommandSpec[] push = session.Commands.Skip(before).ToArray();
        int rowSix = Array.FindIndex(push, command => IsRz090529Row(command, 6));
        Assert.Contains(push.Take(rowSix < 0 ? push.Length : rowSix), IsVaporChamberRead);

        if (change == 0)
        {
            Assert.True(rowSix >= 0);
            Assert.Single(push, IsVaporChamberSelection);
            Assert.NotNull(recordAtSelection);
            await port.RelinquishRendererAsync(default);
            Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        }
        else
        {
            Assert.Equal(-1, rowSix);
            Assert.DoesNotContain(push, IsVaporChamberSet);
            Assert.Null(storage.Journal.Load().Baseline!.VaporChamber);
            await port.RelinquishRendererAsync(default);
            Assert.Equal(external, session.McuVaporChamber);
            Assert.DoesNotContain(session.Commands, IsVaporChamberSet);
        }
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529VaporChamber_SampledEffectReleasesSelectedRegionTakenBeforeFirstSample(bool restorable)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = CreateRz090529Frames(session, policy, new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        Assert.Equal(8, session.McuVaporChamber[2]);
        Assert.NotNull(storage.Journal.Load().Baseline!.VaporChamber);
        byte[] external = restorable ? Rz090529WaveVaporChamber : Rz090529SpectrumVaporChamber;
        session.McuVaporChamber = external.ToArray();
        int before = session.Commands.Count;

        await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);

        Assert.DoesNotContain(session.Commands.Skip(before), IsVaporChamberSet);
        Assert.DoesNotContain(session.Commands.Skip(before), command => IsRz090529Row(command, 6));
        Assert.Null(storage.Journal.Load().Baseline!.VaporChamber);
        Assert.Contains(policy.Snapshot().Observations, item => item.Operation == "VaporChamberEffect"
            && item.Stage == "Restore" && item.FailureCategory == "ExternallyChanged");
        await port.RelinquishRendererAsync(default);
        Assert.Equal(external, session.McuVaporChamber);
        Assert.Single(session.Commands, IsVaporChamberSet); // Only Fire's [0,38,8] selection.
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_UnsampledSelectionThenKeyboardOnlyWritesNothingToRegion()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var ownership = new ExperimentalLightingOwnershipState();
        await using var port = CreateRz090529Frames(session, ownership: ownership);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        Assert.True(ownership.VaporChamberFrames);
        await port.SetLightingAsync(LightingSettingsFactory.Reactive(new(1, 2, 3), 60, ReactiveLightingDuration.Medium), default);
        Assert.False(ownership.VaporChamberFrames);
        await port.RelinquishRendererAsync(default);

        Assert.DoesNotContain(session.Commands, IsVaporChamberSet);
        Assert.DoesNotContain(session.Commands, command => IsRz090529Row(command, 6));
        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529VaporChamber_ExternallyChangedRegionIsLeftUnchanged(bool switchEffects)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var ownership = new ExperimentalLightingOwnershipState { Journal = storage.Journal };
        await using var port = CreateRz090529Frames(session, ownership: ownership);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        session.McuVaporChamber = Rz090529WaveVaporChamber.ToArray();
        int before = session.Commands.Count;
        if (switchEffects)
        {
            // A prior owner's LED38 now reads another restorable record: not mode 8, so not ours.
            await port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default);
            Assert.DoesNotContain(session.Commands.Skip(before), command => IsRz090529Row(command, 6));
            Assert.False(ownership.VaporChamberFrames);
            Assert.Null(storage.Journal.Load().Baseline!.VaporChamber);
        }

        await port.RelinquishRendererAsync(default);

        Assert.Equal(Rz090529WaveVaporChamber, session.McuVaporChamber);
        Assert.Single(session.Commands, IsVaporChamberSet); // Only the [0,38,8] selection.
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_BuiltInTransitionRestoresOwnedRegionAndScalesUniformRow()
    {
        using var storage = new Rz090529LightingJournalFixture();
        byte[] baseline = [0, 38, 2, 0, 0, 2, 1, 2, 3, 4, 5, 6];
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = baseline.ToArray(),
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
        byte[] row = session.Commands.Single(command => IsRz090529Row(command, 6)).Arguments.ToArray();
        // LED38 keeps its own record brightness, so its RGB carries the 50% keyboard brightness.
        Assert.Equal(new byte[] { 0, 0, 0, 5, 4, 4 }, row.Skip(4).Take(6));
        Assert.Equal(new byte[] { 5, 4, 4 }, row.TakeLast(3));
        byte[] keyboardRow = session.Commands.First(command => IsRz090529Row(command, 0)).Arguments.ToArray();
        Assert.Equal(new byte[] { 9, 8, 7 }, keyboardRow.Skip(4).Take(3));
        Assert.Equal(8, session.McuVaporChamber[2]);
        LightingSettings? recordAtRestore = null;
        session.BeforeSubmission = command =>
        {
            if (IsVaporChamberSet(command) && !IsVaporChamberSelection(command))
            {
                recordAtRestore = ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber;
            }
        };

        await port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default);

        Assert.NotNull(recordAtRestore);
        Assert.Equal(baseline, session.McuVaporChamber);
        Assert.Equal(new byte[] { 0, 5, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0 }, session.McuLighting);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(storage.Journal.Load().Baseline);
    }

    [Fact]
    public void Rz090529VaporChamber_RendererScalesTopRowAndExcludesKeyboardOnlyEffects()
    {
        foreach (LightingSettings spatial in new[]
        {
            LightingSettingsFactory.Fire(60),
            LightingSettingsFactory.Wheel(LightingRotationDirection.Counterclockwise, 60),
            LightingSettingsFactory.Tidal(new(11, 22, 33), new(44, 55, 66), false, LightingDirection.Leftward, 60),
            LightingSettingsFactory.SpectrumCycling(60),
        })
        {
            var effect = new ExperimentalGridLightingEffect(spatial, rz090529: true);
            Assert.True(effect.IncludesVaporChamber);
            TimeSpan at = TimeSpan.FromMilliseconds(1234);
            IReadOnlyList<RgbColor> keyboard = effect.RenderFrame(at);
            IReadOnlyList<RgbColor> vaporChamber = effect.RenderVaporChamber(at);
            Assert.Equal(18, vaporChamber.Count);
            Assert.Equal(keyboard.Take(18).Select(color => new RgbColor(
                (byte)((color.Red * 60 + 50) / 100), (byte)((color.Green * 60 + 50) / 100),
                (byte)((color.Blue * 60 + 50) / 100))), vaporChamber);
            Assert.Contains(keyboard.Take(18), color => color != default);
        }

        var full = new ExperimentalGridLightingEffect(LightingSettingsFactory.Fire(100), rz090529: true);
        Assert.Equal(full.RenderFrame(TimeSpan.FromSeconds(2)).Take(18), full.RenderVaporChamber(TimeSpan.FromSeconds(2)));

        var stars = new ExperimentalGridLightingEffect(
            LightingSettingsFactory.StarlightRandom(60, StarlightLightingDuration.Short), rz090529: true);
        Assert.Contains(Enumerable.Range(0, 40), second =>
            stars.RenderVaporChamber(TimeSpan.FromSeconds(second)).Any(color => color != default));

        var ripple = new ExperimentalGridLightingEffect(LightingSettingsFactory.Ripple(new(200, 100, 50), 60), rz090529: true);
        Assert.All(ripple.RenderVaporChamber(TimeSpan.Zero), color => Assert.Equal(default, color));
        ripple.Pulse(1, TimeSpan.Zero); // Logical position (0, 1), directly below VC column 0.
        Assert.NotEqual(default, ripple.RenderVaporChamber(TimeSpan.FromSeconds(1.5 / 18))[0]);

        Assert.False(new ExperimentalGridLightingEffect(
            LightingSettingsFactory.Reactive(new(1, 2, 3), 60, ReactiveLightingDuration.Medium), rz090529: true).IncludesVaporChamber);
        var sequence = new ExperimentalGridLightingEffect(Rz090529Sequence(), rz090529: true);
        Assert.False(sequence.IncludesVaporChamber);
        Assert.Throws<InvalidOperationException>(() => sequence.RenderVaporChamber(TimeSpan.Zero));
        Assert.False(new ExperimentalGridLightingEffect(LightingSettingsFactory.Fire(60)).IncludesVaporChamber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529VaporChamber_KeyboardOnlyEffectRestoresRegionThenFramesCanRetakeIt(bool sequence)
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        Assert.Equal(8, session.McuVaporChamber[2]);
        int beforeKeyboardOnly = session.Commands.Count;

        await port.SetLightingAsync(sequence ? Rz090529Sequence()
            : LightingSettingsFactory.Reactive(new(11, 22, 33), 60, ReactiveLightingDuration.Medium), default);

        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        RazerCommandSpec[] keyboardOnly = session.Commands.Skip(beforeKeyboardOnly).ToArray();
        int restore = Array.FindIndex(keyboardOnly, command => IsVaporChamberSet(command) && command.Arguments.Span[2] == 1);
        int firstRow = Array.FindIndex(keyboardOnly, command => IsRz090529Row(command, 0));
        Assert.InRange(restore, 0, firstRow - 1);
        Assert.DoesNotContain(keyboardOnly, command => IsRz090529Row(command, 6));
        Assert.DoesNotContain(keyboardOnly, IsVaporChamberSelection);

        int beforeRetake = session.Commands.Count;
        await port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default);
        Assert.Equal(8, session.McuVaporChamber[2]);
        Assert.Contains(session.Commands.Skip(beforeRetake), command => IsRz090529Row(command, 6));

        await port.RelinquishRendererAsync(default);
        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
    }

    [Fact]
    public async Task Rz090529VaporChamber_RetakeRefusedWhenRegionChangedDuringKeyboardOnlyEffect()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await port.SetLightingAsync(LightingSettingsFactory.Reactive(new(1, 2, 3), 60, ReactiveLightingDuration.Medium), default);
        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        byte[] external = [0, 38, 1, 0, 0, 1, 9, 9, 9, 0, 0, 0];
        session.McuVaporChamber = external.ToArray();
        int before = session.Commands.Count;

        await port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default);

        Assert.DoesNotContain(session.Commands.Skip(before), command => IsRz090529Row(command, 6));
        Assert.DoesNotContain(session.Commands.Skip(before), IsVaporChamberSet);
        await port.RelinquishRendererAsync(default);
        Assert.Equal(external, session.McuVaporChamber);
    }

    [Fact]
    public async Task Rz090529VaporChamber_NewBaselineObservesRegionAfresh()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await port.RelinquishRendererAsync(default);
        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        session.McuVaporChamber = Rz090529WaveVaporChamber.ToArray();
        int before = session.Commands.Count;

        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        Assert.Contains(session.Commands.Skip(before), command => IsRz090529Row(command, 6));
        await port.RelinquishRendererAsync(default);

        Assert.Equal(Rz090529WaveVaporChamber, session.McuVaporChamber);
    }

    [Fact]
    public async Task Rz090529VaporChamber_RefusedReplacementRollsBackRegionDecision()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        bool present = true;
        var ownership = new ExperimentalLightingOwnershipState();
        await using var port = CreateRz090529Frames(session, ownership: ownership, exactHostStillPresent: () => present);
        await port.SetLightingAsync(LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50), default);
        Assert.True(ownership.VaporChamberFrames);
        RazerCommandSpec? refused = null;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId < 0x80) { refused ??= command; present = false; }
        };

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Reactive(new(1, 2, 3), 50, ReactiveLightingDuration.Medium), default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(refused is { } first && IsRz090529Row(first, 0)); // Refused after the LED38 decision.
        session.BeforeSubmission = null;
        present = true;
        Assert.Equal(LightingRendererOwnershipState.AmbientAwareness, port.OwnershipState);
        Assert.True(ownership.VaporChamberFrames);
        await port.PushUniformLightingFrameAsync(new(new RgbColor(9, 8, 7)), default);
        Assert.Contains(session.Commands, command => IsRz090529Row(command, 6));
        await port.RelinquishRendererAsync(default);
        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
    }

    // 0: the selection itself is uncertain; 1: its confirmation read fails;
    // 2: the confirmation reads another mode because the firmware ignored it.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Rz090529VaporChamber_UnconfirmedSelectionFaultsWithoutReplay(int failure)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
            IgnoreVaporChamberWrites = failure == 2,
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        bool selected = false;
        session.BeforeSubmission = command =>
        {
            if (IsVaporChamberSelection(command))
            {
                selected = true;
                if (failure == 0) { throw new IOException("Simulated LED38 selection uncertainty."); }
            }
            else if (failure == 1 && selected && IsVaporChamberRead(command))
            {
                throw new IOException("Simulated LED38 confirmation failure.");
            }
        };
        await using var port = CreateRz090529Frames(session, policy, new() { Journal = storage.Journal });

        BladeWriteException exception = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));

        Assert.Equal(WriteSubmissionState.Indeterminate, exception.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.NotNull(ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));
        Assert.Equal(LightingRendererOwnershipState.Faulted, await port.RelinquishRendererAsync(default));
        Assert.Equal(count, session.Commands.Count);
        Assert.Equal(failure == 0 ? 0 : 1, session.Commands.Count(IsVaporChamberSelection));
    }

    // Built-in transition and relinquish both keep the LED38 selection record until
    // LED38 is confirmed restored; recovery then lets a built-in choice finish it.
    // 0: the restore write is uncertain; 1: firmware ignores it and LED38 still reads
    // custom mode 8; 2: LED38 reads back a different restorable record.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Rz090529VaporChamber_UnconfirmedRegionRestoreKeepsSelectionRecord(bool builtIn, int failureKind)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = CreateRz090529Frames(session, policy, new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        byte[] other = [0, 38, 1, 0, 0, 1, 9, 9, 9, 0, 0, 0];
        session.BeforeSubmission = command =>
        {
            if (IsVaporChamberSet(command) && !IsVaporChamberSelection(command))
            {
                if (failureKind == 0) { throw new IOException("Simulated LED38 restore uncertainty."); }
                session.IgnoreVaporChamberWrites = true;
                if (failureKind == 2) { session.McuVaporChamber = other.ToArray(); }
            }
        };

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => builtIn
            ? port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default)
            : port.RelinquishRendererAsync(default));

        session.IgnoreVaporChamberWrites = false;
        session.BeforeSubmission = null;
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.False(policy.WritesAllowed);
        Assert.NotNull(ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber);
        Assert.Equal(failureKind == 2 ? 1 : 8, session.McuVaporChamber[2]);
        Assert.Equal(builtIn ? new byte[] { 0, 5, 4, 2, 40, 0, 0, 0, 0, 0, 0, 0 } : Rz090529StaticKeyboard,
            session.McuLighting);

        _ = await port.ReadRecoveryStateAsync(default);
        Assert.True(policy.TryCompleteRecovery(ExperimentalRecoveryScope.All, () => true));
        await port.SetLightingAsync(LightingSettingsFactory.Off(), default);
        // From mode 8 the record is restored; another record means it is no longer ours.
        Assert.Equal(failureKind == 2 ? other : Rz090529StaticVaporChamber, session.McuVaporChamber);
        Assert.Null(ReopenRz090529Journal(storage).Load().Baseline);
    }

    // An LED38 that OpenBlade selected must be read before any later frame write;
    // it never degrades to keyboard-only frames that would leave it unreturned.
    [Fact]
    public async Task Rz090529VaporChamber_UnreadableSelectedRegionStopsBeforeAnyWrite()
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = CreateRz090529Frames(session, policy, new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        session.RejectVaporChamberEffectQuery = true;
        int before = session.Commands.Count;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 60), default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.DoesNotContain(session.Commands.Skip(before), command => command.CommandId < 0x80);
        Assert.NotNull(ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber);
        Assert.Equal(8, session.McuVaporChamber[2]);
        session.RejectVaporChamberEffectQuery = false;
        // The stopped animation faults the owner; nothing was submitted, so writes stay
        // allowed and a built-in choice returns LED38.
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.True(policy.WritesAllowed);
        await port.SetLightingAsync(LightingSettingsFactory.Off(), default);
        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        Assert.Null(ReopenRz090529Journal(storage).Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_UnreadableRegionDuringRelinquishKeepsSelectionRecord()
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = CreateRz090529Frames(session, policy, new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        byte[] owned = session.McuVaporChamber.ToArray();
        session.McuVaporChamber = [0, 38, 8, 0, 0, 0, 0, 0, 0, 0, 0];

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.RelinquishRendererAsync(default));

        session.McuVaporChamber = owned;
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.False(policy.WritesAllowed);
        Assert.NotNull(ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.DoesNotContain(session.Commands, command => IsVaporChamberSet(command) && !IsVaporChamberSelection(command));
    }

    [Fact]
    public async Task Rz090529VaporChamber_RefusedRegionRestoreDuringBuiltInKeepsSelectionRecord()
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        bool present = true;
        await using var port = CreateRz090529Frames(session, policy, new() { Journal = storage.Journal }, () => present);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        session.BeforeSubmission = command =>
        {
            if (IsVaporChamberRead(command)) { present = false; }
        };

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default));

        session.BeforeSubmission = null;
        present = true;
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(8, session.McuVaporChamber[2]);
        Assert.Equal(4, session.McuLighting[2]);
        Assert.NotNull(ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.False(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529VaporChamber_CrashRecoveryUsesSchemaTwoSelectionRecord(bool mcuReset)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await port.DisposeAsync(); // A crash: no restoration.
        Assert.Contains("\"schemaVersion\":2", File.ReadAllText(storage.JournalPath), StringComparison.Ordinal);
        if (mcuReset)
        {
            session.McuVaporChamber = Rz090529SpectrumVaporChamber.ToArray();
            session.McuLighting = Rz090529StaticKeyboard.ToArray();
        }

        ExperimentalLightingOwnershipState restarted = Rz090529FactoryOwnership(ReopenRz090529Journal(storage));
        Assert.Equal((int)LightingRendererOwnershipState.Faulted, restarted.Ownership);
        Assert.NotNull(restarted.Baseline?.VaporChamber);
        await using var recovered = CreateRz090529Frames(session, ownership: restarted);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            recovered.SetLightingAsync(LightingSettingsFactory.Fire(60), default));
        Assert.Equal(LightingRendererOwnershipState.Faulted, await recovered.RelinquishRendererAsync(default));
        Assert.Equal(before, session.Commands.Count);

        await recovered.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Rightward, 50), default);

        if (mcuReset)
        {
            Assert.DoesNotContain(session.Commands.Skip(before), IsVaporChamberSet);
            Assert.Equal(Rz090529SpectrumVaporChamber, session.McuVaporChamber);
        }
        else
        {
            Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        }
        Assert.Equal(LightingRendererOwnershipState.None, recovered.OwnershipState);
        Assert.Null(ReopenRz090529Journal(storage).Load().Baseline);
    }

    // 0: the MCU kept custom mode 8; 1: it reset to the saved destination;
    // 2: it reset to another restorable record.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Rz090529VaporChamber_LowPowerCloseKeepsSelectionForTheNextSession(int resume)
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var ownership = new ExperimentalLightingOwnershipState { Journal = storage.Journal };
        var port = CreateRz090529Frames(session, ownership: ownership);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        int before = session.Commands.Count;
        await port.InvalidateForLowPowerAsync();
        Assert.Equal(before, session.Commands.Count);
        Assert.NotNull(ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber);
        if (resume > 0)
        {
            session.McuVaporChamber = (resume == 1 ? Rz090529StaticVaporChamber : Rz090529WaveVaporChamber).ToArray();
            session.McuLighting = Rz090529StaticKeyboard.ToArray();
        }

        await using var resumed = CreateRz090529Frames(session, ownership: ownership);
        before = session.Commands.Count;
        await resumed.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        Assert.Equal(resume != 2, session.Commands.Skip(before).Any(command => IsRz090529Row(command, 6)));
        Assert.Equal(resume != 2, ownership.VaporChamberFrames);
        await resumed.RelinquishRendererAsync(default);

        Assert.Equal(resume == 2 ? Rz090529WaveVaporChamber : Rz090529StaticVaporChamber, session.McuVaporChamber);
        Assert.Null(ReopenRz090529Journal(storage).Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_LowPowerCloseThenBuiltInRestoresRegion()
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        var ownership = new ExperimentalLightingOwnershipState { Journal = storage.Journal };
        var port = CreateRz090529Frames(session, ownership: ownership);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await port.InvalidateForLowPowerAsync();

        await using var resumed = CreateRz090529Frames(session, ownership: ownership);
        await resumed.SetLightingAsync(LightingSettingsFactory.Off(), default);

        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        Assert.Null(ReopenRz090529Journal(storage).Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_CloseWithRestoreReturnsRegion()
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
        };
        await using var port = CreateRz090529Frames(session, ownership: new() { Journal = storage.Journal });
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);

        await port.InvalidateAsync();

        Assert.Equal(Rz090529StaticVaporChamber, session.McuVaporChamber);
        Assert.Equal(Rz090529StaticKeyboard, session.McuLighting);
        Assert.Null(ReopenRz090529Journal(storage).Load().Baseline);
    }

    [Fact]
    public async Task Rz090529VaporChamber_RowSixFailureMidAnimationFaultsAndKeepsSelectionRecord()
    {
        using var storage = new Rz090529LightingJournalFixture();
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = Rz090529StaticVaporChamber.ToArray(),
            EchoLightingAck = true,
        };
        var clock = new VaporChamberClock();
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = new ExperimentalBladePort(session, new(), Model(0x02C7), policy,
            () => true, () => PowerSource.PluggedIn, sharedLightingOwnership: new() { Journal = storage.Journal },
            lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(60), default);
        await clock.WaitForTimerCountAsync(1);
        int rowSix = 0;
        session.BeforeSubmission = command =>
        {
            if (IsRz090529Row(command, 6) && ++rowSix == 1) { throw new IOException("Simulated LED38 row uncertainty."); }
        };

        clock.Advance(TimeSpan.FromMilliseconds(50));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (port.OwnershipState != LightingRendererOwnershipState.Faulted)
        {
            await Task.Delay(5, timeout.Token);
        }

        Assert.False(policy.WritesAllowed);
        Assert.NotNull(ReopenRz090529Journal(storage).Load().Baseline?.VaporChamber);
        int count = session.Commands.Count;
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Task.Delay(50);
        Assert.Equal(count, session.Commands.Count);
        Assert.Equal(8, session.McuVaporChamber[2]);
    }

    [Fact]
    public void Rz090529VaporChamber_DuplicateSuppressionComparesBothRows()
    {
        RgbColor[] keyboard = [new(1, 2, 3), new(4, 5, 6)];
        RgbColor[] vaporChamber = [new(7, 8, 9)];
        RgbColor[] changed = [new(7, 8, 10)];

        Assert.True(ExperimentalBladePort.IsRepeatedKeyTriggeredFrame(keyboard, vaporChamber, keyboard.ToArray(), vaporChamber.ToArray()));
        Assert.True(ExperimentalBladePort.IsRepeatedKeyTriggeredFrame(keyboard, null, keyboard.ToArray(), null));
        Assert.False(ExperimentalBladePort.IsRepeatedKeyTriggeredFrame(keyboard, changed, keyboard, vaporChamber));
        Assert.False(ExperimentalBladePort.IsRepeatedKeyTriggeredFrame(keyboard, vaporChamber, keyboard, null));
        Assert.False(ExperimentalBladePort.IsRepeatedKeyTriggeredFrame(keyboard, null, keyboard, vaporChamber));
        Assert.False(ExperimentalBladePort.IsRepeatedKeyTriggeredFrame(keyboard, vaporChamber, null, vaporChamber));
        Assert.False(ExperimentalBladePort.IsRepeatedKeyTriggeredFrame(
            keyboard, vaporChamber, [new(1, 2, 3), new(4, 5, 7)], vaporChamber));
    }

    [Fact]
    public async Task Rz090529VaporChamber_KeyTriggeredFramesSubmitRowSixWithKeyboardRows()
    {
        var session = new Session
        {
            McuLighting = Rz090529StaticKeyboard.ToArray(),
            McuVaporChamber = [0, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            EchoLightingAck = true,
        };
        var clock = new VaporChamberClock();
        await using var port = new ExperimentalBladePort(session, new(), Model(0x02C7), new(Model(0x02C7)),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Ripple(new(200, 100, 50), 60), default);
        await clock.WaitForTimerCountAsync(1);
        Assert.Equal(7, session.Commands.Count(IsFrameRow));

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        Assert.Equal(7, session.Commands.Count(IsFrameRow));

        clock.Advance(TimeSpan.FromMilliseconds(4950));
        await clock.WaitForTimerCountAsync(3);
        Assert.Equal(14, session.Commands.Count(IsFrameRow));
        Assert.Equal(2, session.Commands.Count(command => IsRz090529Row(command, 6)));

        await port.RelinquishRendererAsync(default);
        Assert.Equal(new byte[] { 0, 38, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, session.McuVaporChamber);

        static bool IsFrameRow(RazerCommandSpec command) => command.ClassId == 3 && command.CommandId == 0x0B;
    }

    // These MCU models have no host-frame path; their built-in effects never address
    // LED38. The host-frame models are covered in ExperimentalBladeGridLightingTests;
    // the RZ09-0301/0421/0423/0427 frames assert region 5 only in AssertMcuFrameContract.
    [Theory]
    [InlineData(0x02B7)]
    [InlineData(0x02B8)]
    public async Task Rz090529VaporChamber_OtherMcuModelsRejectFramesAndNeverAddressRegion38(int product)
    {
        var session = new Session { EchoLightingAck = true, McuLighting = [0, 5, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        await using var port = Create(product, session);
        Assert.False(port.Capabilities.SupportsUniformLightingFrames);
        Assert.DoesNotContain(LightingEffect.Fire, port.Capabilities.LightingEffects);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(60), default));
        await port.SetLightingAsync(LightingSettingsFactory.Wave(LightingDirection.Leftward, 50), default);
        await port.RelinquishRendererAsync(default);

        Assert.NotEmpty(session.Commands);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 15 && command.Arguments.Span[1] == 38);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 3 && command.CommandId == 0x0B);
    }

    private static ExperimentalLightingBaselineJournal ReopenRz090529Journal(Rz090529LightingJournalFixture storage) =>
        new(Path.GetDirectoryName(storage.JournalPath)!, "RZ09-0529", 0x02C7, _ => { });

    // Mirrors ExperimentalBladeFactory's constructor for 0x02C7.
    private static ExperimentalLightingOwnershipState Rz090529FactoryOwnership(ExperimentalLightingBaselineJournal journal)
    {
        ExperimentalLightingBaselineJournalLoadResult loaded = journal.Load();
        return new()
        {
            Journal = journal,
            JournalInvalid = loaded.Invalid,
            Baseline = loaded.Baseline,
            Ownership = loaded.Invalid || loaded.Baseline is not null
                ? (int)LightingRendererOwnershipState.Faulted : 0,
        };
    }

    private static bool IsRz090529Row(RazerCommandSpec command, int row) =>
        command.ClassId == 3 && command.CommandId == 0x0B && command.Arguments.Span[1] == row;

    private static bool IsVaporChamberSet(RazerCommandSpec command) =>
        command.ClassId == 15 && command.CommandId == 2 && command.Arguments.Span[1] == 38;

    private static bool IsVaporChamberSelection(RazerCommandSpec command) =>
        IsVaporChamberSet(command) && command.Arguments.Span[2] == 8;

    private static bool IsVaporChamberRead(RazerCommandSpec command) =>
        command.ClassId == 15 && command.CommandId == 0x82 && command.Arguments.Span[1] == 38;

    private static string Describe(RazerCommandSpec command) => (command.ClassId, command.CommandId) switch
    {
        (3, 0x0B) => $"03/0B row{command.Arguments.Span[1]}",
        (15, 2) => $"0F/02 [{string.Join(',', command.Arguments.ToArray())}]",
        (15, 0x82) => $"0F/82 region{command.Arguments.Span[1]}",
        _ => $"{command.ClassId:X2}/{command.CommandId:X2}",
    };

    private static LightingSettings Rz090529Sequence()
    {
        var slots = new RgbColor[Rz090529KeyboardLightingLayout.SlotCount];
        slots[1] = new RgbColor(12, 34, 56);
        string timeline = KeyboardLightingSequenceCodec.Encode(
            [new KeyboardLightingSequenceFrame(100, slots)], KeyboardLightingLayoutIds.Rz09_0529);
        return LightingSettingsFactory.Sequence(KeyboardLightingLayoutIds.Rz09_0529, timeline, loop: true, brightness: 80);
    }

    private sealed class VaporChamberClock : TimeProvider
    {
        private long ticks;
        private readonly List<Timer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state, GetTimestamp() + dueTime.Ticks);
            lock (timers) { timers.Add(timer); }
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref ticks, amount.Ticks);
            Timer[] due;
            lock (timers) { due = timers.Where(timer => timer.Due <= GetTimestamp()).ToArray(); }
            foreach (Timer timer in due) { timer.Fire(); }
        }

        internal async Task WaitForTimerCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (timers) { if (timers.Count >= count) { return; } }
                await Task.Delay(1, timeout.Token);
            }
        }

        private sealed class Timer(TimerCallback callback, object? state, long due) : ITimer
        {
            private int completed;
            internal long Due { get; } = due;
            internal void Fire()
            {
                if (Interlocked.Exchange(ref completed, 1) == 0) { callback(state); }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref completed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
