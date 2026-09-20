using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class LightingEditorStateTests
{
    public static IEnumerable<object[]> Settings()
    {
        RgbColor primary = new(12, 34, 56);
        RgbColor secondary = new(78, 90, 123);
        LightingSettings[] settings =
        [
            LightingSettingsFactory.Off(),
            LightingSettingsFactory.Static(primary, 63),
            LightingSettingsFactory.SpectrumCycling(63),
            LightingSettingsFactory.BreathingSingle(primary, 63),
            LightingSettingsFactory.BreathingDual(primary, secondary, 63),
            LightingSettingsFactory.BreathingRandom(63),
            LightingSettingsFactory.Reactive(primary, 63, ReactiveLightingDuration.Long),
            LightingSettingsFactory.Ripple(primary, 63),
            LightingSettingsFactory.Tidal(primary, secondary, false, LightingDirection.Leftward, 63),
            LightingSettingsFactory.StarlightSingle(primary, 63, StarlightLightingDuration.Short),
            LightingSettingsFactory.StarlightDual(primary, secondary, 63, StarlightLightingDuration.Long),
            LightingSettingsFactory.StarlightRandom(63, StarlightLightingDuration.Short),
            LightingSettingsFactory.Wave(LightingDirection.Leftward, 63),
            LightingSettingsFactory.Wheel(LightingRotationDirection.Counterclockwise, 63),
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Bottom, 63),
            LightingSettingsFactory.AudioMeter(2.75, 63, 600),
            LightingSettingsFactory.Fire(63),
            LightingSettingsFactory.Sequence(KeyboardLightingLayoutIds.Rz09_0581, "AQID", false, 63),
        ];
        foreach (LightingSettings setting in settings)
        {
            yield return [setting with { LogoMode = LogoLightingMode.Breathing }];
        }
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public void Load_RoundTripsEveryEffectAndLogoWithoutDirtyingOrRepublishingValues(
        LightingSettings settings)
    {
        var editor = new LightingEditorState();
        editor.Load(settings);
        Assert.Equal(settings, editor.ToSettings());
        Assert.False(editor.IsDirty);
        var changes = new List<LightingEditorChange>();
        editor.Changed += changes.Add;

        editor.Load(settings);

        Assert.Equal(settings, editor.ToSettings());
        Assert.False(editor.IsDirty);
        Assert.DoesNotContain(changes, change => change.Kind != LightingEditorChangeKind.Computed);
        editor.LogoMode = LogoLightingMode.Off;
        LightingSettings edited = editor.ToSettings();
        editor.Load(settings);
        Assert.True(editor.IsDirty); // Loading values does not acknowledge a submitted edit.
        Assert.Equal(settings, editor.ToSettings());
        Assert.NotEqual(settings, edited);
    }

    [Fact]
    public void EffectSelection_PublishesDirtyBeforeAssignmentAndEachVisibleDefaultInOrder()
    {
        var editor = new LightingEditorState();
        var observations = new List<(LightingEditorValue Value, LightingSettings Draft)>();
        editor.Changed += change =>
        {
            if (change.Kind == LightingEditorChangeKind.Value)
            {
                observations.Add((change.Value, editor.ToSettings()));
            }
        };

        editor.Effect = LightingEffect.Static;

        Assert.Equal(LightingEditorValue.Dirty, observations[0].Value);
        Assert.Equal(LightingEffect.Off, observations[0].Draft.Effect);
        Assert.Equal(LightingEditorValue.Effect, observations[1].Value);
        Assert.Equal(default, observations[1].Draft.Color);
        Assert.Equal(50, observations[1].Draft.Brightness);
        Assert.Equal(
            new[] { default(RgbColor), default, LightingSettingsFactory.DefaultVisibleColor },
            observations.Where(item => item.Value == LightingEditorValue.Draft)
                .Select(item => item.Draft.Color));
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void ColorEdit_PreservesChannelPublicationOrderAndIgnoresEquivalentEdits()
    {
        var editor = new LightingEditorState();
        editor.Load(LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 60));
        var observations = new List<(LightingEditorChange Change, RgbColor Color)>();
        editor.Changed += change => observations.Add((change, editor.PrimaryColor));

        editor.PrimaryColor = new RgbColor(4, 5, 6);

        Assert.Equal(new[] { LightingEditorValue.Dirty, LightingEditorValue.Red,
            LightingEditorValue.Green, LightingEditorValue.Blue, LightingEditorValue.PrimaryColor,
            LightingEditorValue.Draft }, observations
                .Where(item => item.Change.Kind == LightingEditorChangeKind.Value)
                .Select(item => item.Change.Value));
        Assert.Equal(new RgbColor(1, 2, 3), observations[0].Color);
        Assert.Equal(new RgbColor(4, 2, 3), observations[1].Color);
        Assert.Equal(new RgbColor(4, 5, 3), observations[2].Color);
        observations.Clear();
        editor.PrimaryColor = new RgbColor(4, 5, 6);
        Assert.Empty(observations);
    }

    [Fact]
    public void UnconfirmedZeroBrightness_UsesVisibleDefaultOnlyForStatusLoad()
    {
        var editor = new LightingEditorState();
        editor.SetEffectUnconfirmed(true);
        editor.Load(LightingSettingsFactory.Off());
        Assert.Equal(0, editor.Brightness);
        editor.Load(LightingSettingsFactory.Off(), startUnconfirmedEffectAtDefaultBrightness: true);
        Assert.Equal(50, editor.Brightness);
        Assert.True(editor.IsEffectUnconfirmed);
        Assert.False(editor.IsDirty);
        editor.Effect = LightingEffect.Static;
        Assert.False(editor.IsEffectUnconfirmed);
        Assert.Equal(LightingSettingsFactory.Static(LightingSettingsFactory.DefaultVisibleColor, 50),
            editor.ToSettings());
    }

    [Fact]
    public void ImportedSequence_SurvivesOtherEffectsAndPreservesItsParameters()
    {
        var editor = new LightingEditorState();
        LightingSettings imported = LightingSettingsFactory.Sequence(
            KeyboardLightingLayoutIds.Rz09_0581, "AQID", false, 77)
            with
        { LogoMode = LogoLightingMode.Off };
        editor.Load(imported);
        editor.Load(LightingSettingsFactory.Wave(LightingDirection.Leftward, 43)
            with
        { LogoMode = LogoLightingMode.Static });
        editor.Effect = LightingEffect.Sequence;
        Assert.Equal(imported with { Brightness = 43, LogoMode = LogoLightingMode.Static },
            editor.ToSettings());
    }

    [Fact]
    public void CompletionAndPowerSource_OnlyReleaseTheApplicableDraft()
    {
        var editor = new LightingEditorState();
        editor.PrepareForPowerSource(PowerSource.PluggedIn);
        editor.Effect = LightingEffect.Static;
        LightingSettings submitted = editor.ToSettings();
        editor.Red = 12;
        Assert.False(editor.ClearDirtyIfUnchanged(submitted));
        Assert.True(editor.IsDirty);
        editor.PrepareForPowerSource(PowerSource.ExternalUnknown);
        editor.PrepareForPowerSource(PowerSource.PluggedIn);
        Assert.True(editor.IsDirty);
        Assert.True(editor.ClearDirtyIfUnchanged(editor.ToSettings()));
        Assert.False(editor.IsDirty);
        editor.Red = 13;
        editor.PrepareForPowerSource(PowerSource.Battery);
        Assert.False(editor.IsDirty);
        Assert.Equal(13, editor.Red); // The caller now loads the accepted source's values.
    }

    [Fact]
    public void RandomTidalLoad_PreservesExistingTopLevelColorDecoding()
    {
        var editor = new LightingEditorState();
        var primary = new RgbColor(12, 34, 56);
        var secondary = new RgbColor(78, 90, 123);
        LightingSettings incoming = LightingSettingsFactory.Tidal(
            primary, secondary, true, LightingDirection.Leftward, 63);

        editor.Load(incoming);

        // Characterize the pre-extraction decoder: random Tidal's top-level
        // Color is black, even when its inactive parameter color is nonzero.
        Assert.Equal(default, editor.PrimaryColor);
        Assert.Equal(LightingSettingsFactory.Tidal(
            default, secondary, true, LightingDirection.Leftward, 63), editor.ToSettings());
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void ExplicitSelectionOfHiddenDesiredEffect_SeedsDefaultsAndMarksIntent()
    {
        var editor = new LightingEditorState();
        editor.Load(LightingSettingsFactory.Static(default, 0));
        editor.SetEffectUnconfirmed(true);
        editor.SelectEffect(LightingEffect.Static);
        Assert.False(editor.IsEffectUnconfirmed);
        Assert.True(editor.IsDirty);
        Assert.Equal(50, editor.Brightness);
        // Retain existing static-color behavior; selecting this same effect
        // does not replace an intentional black color with another color.
        Assert.Equal(default, editor.PrimaryColor);
    }

    [Fact]
    public void DualColorDefaults_AreSeededWithoutReplacingExistingColors()
    {
        var editor = new LightingEditorState();
        editor.Effect = LightingEffect.Breathing;
        editor.BreathingColorMode = BreathingColorMode.Dual;
        Assert.Equal(LightingSettingsFactory.DefaultVisibleColor, editor.PrimaryColor);
        Assert.Equal(new RgbColor(255, 0, 170), editor.SecondaryColor);
        editor.PrimaryColor = new RgbColor(11, 22, 33);
        editor.SecondaryColor = new RgbColor(44, 55, 66);
        editor.Effect = LightingEffect.Starlight;
        editor.StarlightColorMode = StarlightColorMode.Dual;
        Assert.Equal(new RgbColor(11, 22, 33), editor.PrimaryColor);
        Assert.Equal(new RgbColor(44, 55, 66), editor.SecondaryColor);
    }

    [Fact]
    public void InvalidValues_DoNotChangeOrDirtyTheEditor()
    {
        var editor = new LightingEditorState();
        editor.Load(LightingSettingsFactory.AudioMeter(2, 50, 600));
        LightingSettings before = editor.ToSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Brightness = 101);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Red = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AudioMeterColorBoost = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AudioMeterColorBoost = 1.1);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AudioMeterDecayMilliseconds = 601);
        editor.Effect = (LightingEffect)999;
        editor.LogoMode = (LogoLightingMode)999;
        editor.Direction = (LightingDirection)999;
        Assert.Equal(before, editor.ToSettings());
        Assert.False(editor.IsDirty);
    }
}
