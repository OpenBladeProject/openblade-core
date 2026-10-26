using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ApplicationProfileValidationTests
{
    [Fact]
    public void ValidateRejectsDuplicateExecutableAndOutOfRangeBrightness()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        static ApplicationProfileDefinition Definition(Guid id, string path, byte brightness) =>
            new(
                id,
                "Game",
                path,
                ApplicationActivationMode.Foreground,
                new ApplicationProfileOverrideSet
                {
                    PluggedIn = new ApplicationPowerProfileOverride
                    {
                        Display = DisplayProfileSettings.Unconfigured() with
                        {
                            BrightnessPercent = brightness,
                        },
                    },
                });
        ProfileSetValidator validator = ProfileSetValidator.CreateUnverifiedSafeDefaults();

        Assert.Throws<InvalidDataException>(() => validator.Validate(defaults with
        {
            ApplicationProfiles =
            [
                Definition(Guid.NewGuid(), @"C:\Games\game.exe", 50),
                Definition(Guid.NewGuid(), @"C:\Games\GAME.exe", 50),
            ],
        }));
        Assert.Throws<InvalidDataException>(() => validator.Validate(defaults with
        {
            ApplicationProfiles =
            [
                Definition(Guid.NewGuid(), @"C:\Games\game.exe", 101),
            ],
        }));
    }
}
