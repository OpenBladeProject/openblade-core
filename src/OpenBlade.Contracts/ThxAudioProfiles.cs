namespace OpenBlade.Contracts;

public enum ThxEqualizerPreset
{
    Game,
    Movie,
    Music,
    Voice,
    Custom,
}

public enum ThxApplicationAudioOutput
{
    Game,
    Movie,
    Music,
    Voice,
    Custom,
    Immersive,
    Competitive,
}

public sealed record ThxAudioEnhancementSettings(bool Enabled, int LevelPercent);

public sealed record ThxEqualizerCurves(
    IReadOnlyList<double> Game,
    IReadOnlyList<double> Movie,
    IReadOnlyList<double> Music,
    IReadOnlyList<double> Voice,
    IReadOnlyList<double> Custom);

public sealed record ThxAudioEndpointSettings(
    bool SpatialEnabled,
    bool EqualizerEnabled,
    ThxEqualizerPreset SelectedPreset,
    ThxEqualizerCurves EqualizerCurves,
    ThxAudioEnhancementSettings VoiceClarity,
    ThxAudioEnhancementSettings? SoundNormalization);

public sealed record ThxApplicationAudioRule
{
    public const int MaximumExecutableNameLength = 260;

    public ThxApplicationAudioRule(string executableName, ThxApplicationAudioOutput output)
    {
        ExecutableName = NormalizeExecutableName(executableName);
        Output = output;
    }

    public string ExecutableName { get; }

    public ThxApplicationAudioOutput Output { get; }

    public static string NormalizeExecutableName(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        string normalized = executableName.Trim().ToLowerInvariant();
        if (normalized.Length > MaximumExecutableNameLength
            || normalized.Any(char.IsControl)
            || normalized.Any(static character => "<>:\"/\\|?*".Contains(character))
            || normalized is "." or ".."
            || normalized.EndsWith(".", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A THX application rule requires one executable file name.",
                nameof(executableName));
        }

        return normalized;
    }
}

public sealed record ThxAudioProfileSettings(
    bool ApplicationRoutingEnabled,
    ThxAudioEndpointSettings Speakers,
    ThxAudioEndpointSettings Headphones,
    IReadOnlyList<ThxApplicationAudioRule> ApplicationRules)
{
    public const int SpeakerEqualizerBandCount = 7;
    public const int HeadphoneEqualizerBandCount = 10;
    public const int MaximumApplicationRules = 256;
    public const double MinimumEqualizerGainDb = -12;
    public const double MaximumEqualizerGainDb = 12;
    public const int MinimumEnhancementLevelPercent = 0;
    public const int MaximumEnhancementLevelPercent = 100;

    public static bool IsWellFormed(ThxAudioProfileSettings? value) =>
        value is not null
        && IsWellFormedEndpoint(value.Speakers, SpeakerEqualizerBandCount, true)
        && IsWellFormedEndpoint(value.Headphones, HeadphoneEqualizerBandCount, false)
        && value.ApplicationRules is not null
        && value.ApplicationRules.Count <= MaximumApplicationRules
        && value.ApplicationRules.All(IsWellFormedRule)
        && value.ApplicationRules
            .Select(rule => rule.ExecutableName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == value.ApplicationRules.Count;

    private static bool IsWellFormedEndpoint(
        ThxAudioEndpointSettings? value,
        int expectedBandCount,
        bool requireSoundNormalization) =>
        value is not null
        && Enum.IsDefined(value.SelectedPreset)
        && value.EqualizerCurves is not null
        && IsWellFormedEnhancement(value.VoiceClarity)
        && requireSoundNormalization == (value.SoundNormalization is not null)
        && (value.SoundNormalization is null
            || IsWellFormedEnhancement(value.SoundNormalization))
        && new[]
        {
            value.EqualizerCurves.Game,
            value.EqualizerCurves.Movie,
            value.EqualizerCurves.Music,
            value.EqualizerCurves.Voice,
            value.EqualizerCurves.Custom,
        }.All(curve => curve is not null
            && curve.Count == expectedBandCount
            && curve.All(gain => double.IsFinite(gain)
                && gain is >= MinimumEqualizerGainDb and <= MaximumEqualizerGainDb));

    private static bool IsWellFormedEnhancement(ThxAudioEnhancementSettings? value) =>
        value?.LevelPercent is
            >= MinimumEnhancementLevelPercent and <= MaximumEnhancementLevelPercent;

    private static bool IsWellFormedRule(ThxApplicationAudioRule? rule)
    {
        if (rule is null || !Enum.IsDefined(rule.Output))
        {
            return false;
        }

        try
        {
            return string.Equals(
                rule.ExecutableName,
                ThxApplicationAudioRule.NormalizeExecutableName(rule.ExecutableName),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public static class ThxAudioProductIds
{
    public const string Rznb12 = "rznb12";
}

public sealed record ThxAudioCapabilities(
    DeviceFeatureAccess Access,
    string ProductId)
{
    internal bool IsWellFormed =>
        Enum.IsDefined(Access)
        && string.Equals(ProductId, ThxAudioProductIds.Rznb12, StringComparison.Ordinal);
}
