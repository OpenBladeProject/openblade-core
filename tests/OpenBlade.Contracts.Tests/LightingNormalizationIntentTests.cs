namespace OpenBlade.Contracts.Tests;

public sealed class LightingNormalizationIntentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyOffNormalizationPreservesAllIntentMetadata(bool enabled)
    {
        var input = new LightingSettings(LightingEffect.Off, default, 0)
        {
            LogoMode = LogoLightingMode.Static,
            IndependentControls = new(42, LogoLightingMode.Breathing),
            ControlsKeyboardEffect = enabled,
        };
        LightingSettings expected = input with { Parameters = LightingSettingsFactory.Off().Parameters };
        Assert.Equal(expected, LightingSettingsFactory.NormalizeLegacyOff(input));
        Assert.Equal(expected, LightingSettingsFactory.NormalizeAudioMeterDecay(input));
        Assert.Equal(expected, LightingSettingsFactory.NormalizeLegacyInvisibleEffect(input));
        Assert.Null(input.Parameters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryInvisibleEffectReconstructionPreservesIntentMetadata(bool enabled)
    {
        string timeline = KeyboardLightingSequenceCodec.Encode(
        [
            new KeyboardLightingSequenceFrame(100, new RgbColor[AmbientLightingFrame.SlotCount]),
        ]);
        LightingSettings[] inputs =
        [
            LightingSettingsFactory.Static(default, 0),
            LightingSettingsFactory.SpectrumCycling(0),
            LightingSettingsFactory.BreathingSingle(default, 0),
            LightingSettingsFactory.BreathingDual(default, default, 0),
            LightingSettingsFactory.BreathingRandom(0),
            LightingSettingsFactory.Reactive(default, 0, ReactiveLightingDuration.Medium),
            LightingSettingsFactory.Tidal(default, default, false, LightingDirection.Leftward, 0),
            LightingSettingsFactory.StarlightSingle(default, 0, StarlightLightingDuration.Medium),
            LightingSettingsFactory.StarlightDual(default, default, 0, StarlightLightingDuration.Medium),
            LightingSettingsFactory.StarlightRandom(0, StarlightLightingDuration.Medium),
            LightingSettingsFactory.Wave(LightingDirection.Leftward, 0),
            LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 0),
            LightingSettingsFactory.Ripple(default, 0),
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 0),
            LightingSettingsFactory.AudioMeter(1, 0, 375),
            LightingSettingsFactory.Fire(0),
            LightingSettingsFactory.Sequence(KeyboardLightingLayoutIds.Rz09_0581, timeline, true, 0),
            new(LightingEffect.Static, default, 0),
            new(LightingEffect.Spectrum, default, 0),
            new(LightingEffect.Breathing, default, 0),
            new(LightingEffect.Reactive, default, 0),
        ];
        foreach (LightingSettings original in inputs)
        {
            LightingSettings input = original with
            {
                LogoMode = LogoLightingMode.Static,
                IndependentControls = new(42, LogoLightingMode.Breathing),
                ControlsKeyboardEffect = enabled,
            };
            LightingSettings normalized = LightingSettingsFactory.NormalizeLegacyInvisibleEffect(input);
            Assert.Equal(input.Effect, normalized.Effect);
            Assert.Equal(LightingSettingsFactory.DefaultVisibleBrightness, normalized.Brightness);
            Assert.Equal(input.LogoMode, normalized.LogoMode);
            Assert.Equal(input.IndependentControls, normalized.IndependentControls);
            Assert.Equal(enabled, normalized.ControlsKeyboardEffect);
            Assert.True(LightingSettingsValidation.IsWellFormed(normalized));
        }
    }

    [Fact]
    public void AudioDecayAndVisibleNoOpRetainIntent()
    {
        LightingSettings input = LightingSettingsFactory.AudioMeter(1, 50, 375) with
        {
            ControlsKeyboardEffect = true,
            IndependentControls = new(42, null),
            LogoMode = LogoLightingMode.Off,
        };
        LightingSettings normalized = LightingSettingsFactory.NormalizeAudioMeterDecay(input);
        Assert.Equal(input with { Parameters = new AudioMeterLightingParameters(1, 500) }, normalized);
        LightingSettings visible = LightingSettingsFactory.Static(new(1, 2, 3), 65) with { ControlsKeyboardEffect = true };
        Assert.Same(visible, LightingSettingsFactory.NormalizeLegacyInvisibleEffect(visible));
    }
}
