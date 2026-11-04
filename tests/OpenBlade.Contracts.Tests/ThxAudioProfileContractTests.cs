using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ThxAudioProfileContractTests
{
    [Theory]
    [InlineData(ThxEqualizerPreset.Game)]
    [InlineData(ThxEqualizerPreset.Movie)]
    [InlineData(ThxEqualizerPreset.Music)]
    [InlineData(ThxEqualizerPreset.Voice)]
    [InlineData(ThxEqualizerPreset.Custom)]
    public void EqualizerValueEqualityDetectsEditsToEveryPreset(ThxEqualizerPreset preset)
    {
        var original = new ThxEqualizerCurves(new double[7], new double[7], new double[7],
            new double[7], new double[7]);
        ThxEqualizerCurves copy = System.Text.Json.JsonSerializer.Deserialize<ThxEqualizerCurves>(
            System.Text.Json.JsonSerializer.Serialize(original))!;
        double[] edited = [0, 0, 0, 1.5, 0, 0, 0];
        ThxEqualizerCurves changed = preset switch
        {
            ThxEqualizerPreset.Game => copy with { Game = edited },
            ThxEqualizerPreset.Movie => copy with { Movie = edited },
            ThxEqualizerPreset.Music => copy with { Music = edited },
            ThxEqualizerPreset.Voice => copy with { Voice = edited },
            ThxEqualizerPreset.Custom => copy with { Custom = edited },
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        Assert.Equal(original, copy);
        Assert.NotEqual(original, changed);
        Assert.NotEqual(changed, original);
    }

    [Fact]
    public void AudioProfileRoundTripPreservesValueEqualityAndHashCodes()
    {
        static ThxAudioEndpointSettings Endpoint(int count, bool speaker) => new(
            true, true, ThxEqualizerPreset.Music,
            new(new double[count], new double[count], new double[count], new double[count], new double[count]),
            new(true, 50), speaker ? new(false, 30) : null);
        var original = new ThxAudioProfileSettings(true, Endpoint(7, true), Endpoint(10, false),
            [new("game.exe", ThxApplicationAudioOutput.Immersive)]);
        ThxAudioProfileSettings copy = System.Text.Json.JsonSerializer.Deserialize<ThxAudioProfileSettings>(
            System.Text.Json.JsonSerializer.Serialize(original))!;

        Assert.NotSame(original.ApplicationRules, copy.ApplicationRules);
        Assert.NotSame(original.Speakers.EqualizerCurves.Game, copy.Speakers.EqualizerCurves.Game);
        Assert.Equal(original, copy);
        Assert.Equal(original.GetHashCode(), copy.GetHashCode());
        Assert.Equal(original.Speakers.EqualizerCurves.GetHashCode(), copy.Speakers.EqualizerCurves.GetHashCode());
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        Assert.Equal(profiles with { PluggedIn = profiles.PluggedIn with { ThxAudio = original } },
            profiles with { PluggedIn = profiles.PluggedIn with { ThxAudio = copy } });
        Assert.NotEqual(original, copy with { ApplicationRoutingEnabled = false });
        Assert.NotEqual(original, copy with { ApplicationRules = [new("game.exe", ThxApplicationAudioOutput.Game)] });
        Assert.NotEqual(original, copy with { Speakers = copy.Speakers with { SpatialEnabled = false } });
        Assert.NotEqual(original, copy with { Headphones = copy.Headphones with { EqualizerEnabled = false } });
    }

    [Theory]
    [InlineData('a')]
    [InlineData('界')]
    public void ApplicationRules_RespectEncodedByteBudget(char character)
    {
        var rules = new List<ThxApplicationAudioRule>();
        while (true)
        {
            var candidate = new ThxApplicationAudioRule(
                rules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + new string(character, 240) + ".exe", ThxApplicationAudioOutput.Competitive);
            ThxApplicationAudioRule[] next = [.. rules, candidate];
            if (!ThxAudioProfileSettings.ApplicationRulesFitWithinByteLimit(next))
            {
                Assert.NotEmpty(rules);
                Assert.True(ThxAudioProfileSettings.ApplicationRulesFitWithinByteLimit(rules));
                Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(next).Length
                    > ThxAudioProfileSettings.MaximumApplicationRuleBytes);
                break;
            }
            rules.Add(candidate);
        }
    }

    [Fact]
    public void ApplicationRule_NormalizesExecutableBasename()
    {
        var rule = new ThxApplicationAudioRule(
            "  ExampleGame.EXE  ",
            ThxApplicationAudioOutput.Immersive);

        Assert.Equal("examplegame.exe", rule.ExecutableName);
    }

    [Theory]
    [InlineData("bad<name.exe")]
    [InlineData("bad>name.exe")]
    [InlineData("bad:name.exe")]
    [InlineData("bad\"name.exe")]
    [InlineData("folder/name.exe")]
    [InlineData("folder\\name.exe")]
    [InlineData("bad|name.exe")]
    [InlineData("bad?name.exe")]
    [InlineData("bad*name.exe")]
    [InlineData("bad\u0001name.exe")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("name.")]
    public void ApplicationRule_RejectsInvalidWindowsBasename(string executableName)
    {
        Assert.Throws<ArgumentException>(() => new ThxApplicationAudioRule(
            executableName,
            ThxApplicationAudioOutput.Game));
    }
}
