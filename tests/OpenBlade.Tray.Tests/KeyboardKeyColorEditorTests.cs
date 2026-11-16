using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class KeyboardKeyColorEditorTests
{
    [Fact]
    public void SetKeyColor_BuildsAKeyboardOnlySequenceWithIndependentEditableSlots()
    {
        var editor = new KeyboardKeyColorEditor();
        var qColor = new RgbColor(20, 40, 60);
        var enterColor = new RgbColor(80, 100, 120);

        editor.SetKeyColor(40, qColor);
        editor.SetKeyColor(69, enterColor);

        LightingSettings settings = editor.BuildLightingSettings(75);
        Assert.True(LightingSettingsValidation.IsWellFormed(settings));
        SequenceLightingParameters parameters = Assert.IsType<SequenceLightingParameters>(settings.Parameters);
        Assert.Equal(KeyboardLightingLayoutIds.Rz09_0529, parameters.LayoutId);
        KeyboardLightingSequenceFrame frame = Assert.Single(
            KeyboardLightingSequenceCodec.Decode(parameters.TimelineBase64, parameters.LayoutId).Frames);
        Assert.Equal(KeyboardKeyColorEditor.DefaultFrameDurationMilliseconds, frame.DurationMilliseconds);
        Assert.Equal(qColor, frame.Slots[40]);
        Assert.Equal(enterColor, frame.Slots[69]);
        Assert.Equal(LightingSettingsFactory.DefaultVisibleColor, frame.Slots[41]);
        Assert.Equal(default, frame.Slots[0]);
        Assert.Equal(Rz090529KeyboardLightingLayout.SlotCount, frame.Slots.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(114)]
    public void SetKeyColor_RejectsNonKeySlots(int slot)
    {
        var editor = new KeyboardKeyColorEditor();

        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetKeyColor(slot, new(1, 2, 3)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(255)]
    public void BuildLightingSettings_RejectsInvalidBrightness(int brightness)
    {
        var editor = new KeyboardKeyColorEditor();

        Assert.Throws<ArgumentOutOfRangeException>(() => editor.BuildLightingSettings((byte)brightness));
    }

    [Fact]
    public void ExistingStaticDraft_SeedsKeysAndRetainsBrightnessWithoutMutatingOriginal()
    {
        var original = LightingSettingsFactory.Static(new(15, 30, 45), 42);
        var editor = new KeyboardKeyColorEditor(original);
        Assert.False(editor.HasChanges);
        Assert.Equal(original, editor.BuildLightingSettings());
        Assert.Equal(original.Color, editor.Colors[41]);

        editor.SetKeyColor(40, new(60, 70, 80));
        LightingSettings result = editor.BuildLightingSettings();
        var parameters = Assert.IsType<SequenceLightingParameters>(result.Parameters);
        var frame = Assert.Single(KeyboardLightingSequenceCodec.Decode(
            parameters.TimelineBase64, parameters.LayoutId).Frames);
        Assert.Equal(42, result.Brightness);
        Assert.Equal(original.Color, frame.Slots[41]);
        Assert.Equal(new RgbColor(60, 70, 80), frame.Slots[40]);
        Assert.Equal(new RgbColor(15, 30, 45), original.Color);
    }

    [Fact]
    public void ExistingAnimation_PreservesUntouchedKeysDurationsAndLoopWhenPinningAKey()
    {
        var first = new RgbColor[Rz090529KeyboardLightingLayout.SlotCount];
        var second = new RgbColor[Rz090529KeyboardLightingLayout.SlotCount];
        first[40] = new(1, 2, 3);
        first[41] = new(4, 5, 6);
        second[40] = new(7, 8, 9);
        second[41] = new(10, 11, 12);
        string timeline = KeyboardLightingSequenceCodec.Encode(
            [new(100, first), new(250, second)], KeyboardLightingLayoutIds.Rz09_0529);
        var original = LightingSettingsFactory.Sequence(
            KeyboardLightingLayoutIds.Rz09_0529, timeline, loop: false, brightness: 31);
        var editor = new KeyboardKeyColorEditor(original);
        Assert.Equal(first[40], editor.Colors[40]);
        Assert.Equal(original, editor.BuildLightingSettings());

        var chosen = new RgbColor(70, 80, 90);
        editor.SetKeyColor(40, chosen);
        LightingSettings result = editor.BuildLightingSettings();
        var parameters = Assert.IsType<SequenceLightingParameters>(result.Parameters);
        var frames = KeyboardLightingSequenceCodec.Decode(parameters.TimelineBase64, parameters.LayoutId).Frames;
        Assert.False(parameters.Loop);
        Assert.Equal(31, result.Brightness);
        Assert.Equal(new[] { 100, 250 }, frames.Select(frame => frame.DurationMilliseconds));
        Assert.All(frames, frame => Assert.Equal(chosen, frame.Slots[40]));
        Assert.Equal(first[41], frames[0].Slots[41]);
        Assert.Equal(second[41], frames[1].Slots[41]);
        Assert.Equal(timeline, Assert.IsType<SequenceLightingParameters>(original.Parameters).TimelineBase64);

        var reopened = new KeyboardKeyColorEditor(result);
        Assert.Equal(chosen, reopened.Colors[40]);
        Assert.Equal(result, reopened.BuildLightingSettings());
    }

    [Fact]
    public void UnchangedEffectOrRevertedEdit_PreservesOriginalDraft()
    {
        var original = LightingSettingsFactory.SpectrumCycling(19);
        var editor = new KeyboardKeyColorEditor(original);
        Assert.Equal(original, editor.BuildLightingSettings());
        RgbColor initial = editor.Colors[40];
        editor.SetKeyColor(40, new(10, 20, 30));
        Assert.True(editor.HasChanges);
        editor.SetKeyColor(40, initial);
        Assert.False(editor.HasChanges);
        Assert.Equal(original, editor.BuildLightingSettings());
    }

    [Fact]
    public void Off_IsPreservedUntilAnExplicitKeyEditEnablesVisibleLighting()
    {
        var editor = new KeyboardKeyColorEditor(LightingSettingsFactory.Off());
        Assert.Equal(LightingSettingsFactory.Off(), editor.BuildLightingSettings());
        Assert.Equal(default, editor.Colors[40]);
        editor.SetKeyColor(40, new(1, 2, 3));
        LightingSettings result = editor.BuildLightingSettings();
        Assert.Equal(LightingSettingsFactory.DefaultVisibleBrightness, result.Brightness);
        Assert.True(LightingSettingsValidation.IsWellFormed(result));
    }

    [Fact]
    public void IndependentControlDraft_IsNotDroppedWhenEditingKeyboardColors()
    {
        var controls = new IndependentLightingSettings(Brightness: 22, VaporChamberLightingEnabled: true);
        var original = LightingSettingsFactory.Static(new(1, 2, 3), 43) with
        {
            IndependentControls = controls,
        };
        var editor = new KeyboardKeyColorEditor(original);
        editor.SetKeyColor(40, new(4, 5, 6));
        LightingSettings result = editor.BuildLightingSettings();
        Assert.Equal(controls, result.IndependentControls);
        Assert.True(result.ControlsKeyboardEffect);
        Assert.True(LightingSettingsValidation.IsWellFormed(result));
    }

    [Fact]
    public void Constructor_RejectsInvalidDraftAndOtherModelsSequence()
    {
        Assert.Throws<ArgumentException>(() => new KeyboardKeyColorEditor(
            LightingSettingsFactory.Static(new(1, 2, 3), 0)));
        string timeline = KeyboardLightingSequenceCodec.Encode(
            [new(100, new RgbColor[AmbientLightingFrame.SlotCount])]);
        Assert.Throws<ArgumentException>(() => new KeyboardKeyColorEditor(
            LightingSettingsFactory.Sequence(KeyboardLightingLayoutIds.Rz09_0581,
                timeline, loop: true, brightness: 50)));
    }

    [Theory]
    [InlineData(96, "LCtrl")]
    [InlineData(54, "Num7")]
    [InlineData(92, "Num1")]
    [InlineData(111, "Num0")]
    [InlineData(112, "Num.")]
    [InlineData(107, "←")]
    [InlineData(108, "↑")]
    [InlineData(109, "→")]
    [InlineData(110, "↓")]
    [InlineData(113, "Num↵")]
    [InlineData(100, "#101")]
    public void KeyLabels_UseReviewedNamesAndPreferArrowsToNavigationAliases(int slot, string label)
    {
        Assert.Equal(label, KeyboardKeyColorEditor.GetKeyLabel(slot));
    }

    [Fact]
    public void KeyLabels_CoverAllEditableSlotsWithoutOpaqueKOrScanCodeLabels()
    {
        for (int slot = 0; slot < Rz090529KeyboardLightingLayout.SlotCount; slot++)
        {
            if (!Rz090529KeyboardLightingLayout.IsKeySlot(slot))
            {
                continue;
            }
            string label = KeyboardKeyColorEditor.GetKeyLabel(slot);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.DoesNotMatch("^[KE][0-9]+$", label);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyboardKeyColorEditor.GetKeyLabel(0));
    }
}
