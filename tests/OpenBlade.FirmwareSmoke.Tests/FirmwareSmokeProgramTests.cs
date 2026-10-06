using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;

namespace OpenBlade.FirmwareSmoke.Tests;

public sealed class FirmwareSmokeProgramTests
{
    [Fact]
    public void CpuVoltageObserveOnlyIsExplicitAndStrictlyParsed()
    {
        string[] args = ["cpu-voltage-optimizer", "--observe-only", "--confirm-target", CpuVoltageOptimizerSmokeRunner.ObserveConfirmation,
            "--expected-service-version", "0.24.0+12345678", "--output", "new-directory"];
        Assert.True(FirmwareSmokeProgram.ReadCpuVoltageObserveOnly(args));
        Assert.False(FirmwareSmokeProgram.ReadCpuVoltageObserveOnly(args.Where(value => value != "--observe-only").ToArray()));
        Assert.Throws<ArgumentException>(() => FirmwareSmokeProgram.ReadCpuVoltageObserveOnly([.. args, "--observe-only"]));
        Assert.Throws<ArgumentException>(() => FirmwareSmokeProgram.ReadCpuVoltageObserveOnly([.. args, "--unknown"]));
        Assert.Throws<ArgumentException>(() => FirmwareSmokeProgram.ReadCpuVoltageObserveOnly(["cpu-voltage-optimizer", "--output", "--observe-only"]));
    }

    [Theory]
    [InlineData("RZ09-0581", "RZ09-0581-FIRMWARE-SMOKE")]
    [InlineData("RZ09-0528", "RZ09-0528-FIRMWARE-SMOKE")]
    [InlineData("rz09-05289en4", "RZ09-0528-FIRMWARE-SMOKE")]
    public void TargetConfirmation_UsesCanonicalExactModel(
        string modelNumber,
        string expected)
    {
        Assert.Equal(expected, FirmwareSmokeProgram.TargetConfirmation(modelNumber));
    }

    [Fact]
    public void TargetConfirmationFormat_DocumentsModelSpecificToken()
    {
        Assert.Equal(
            "<MODEL>-FIRMWARE-SMOKE",
            FirmwareSmokeProgram.TargetConfirmationFormat);
        Assert.Equal(
            "RZ09-0528-FIRMWARE-SMOKE",
            FirmwareSmokeProgram.TargetConfirmation(
                RazerBladeModelCatalog.Rz090528ModelNumber));
    }

    [Theory]
    [InlineData("RZ09-0581", "RZ09-0581-THERMAL-EXTERNAL-DRIFT")]
    [InlineData("rz09-05819en1", "RZ09-0581-THERMAL-EXTERNAL-DRIFT")]
    public void ThermalExternalDriftConfirmation_UsesCanonicalExactModel(
        string modelNumber,
        string expected)
    {
        Assert.Equal(
            expected,
            FirmwareSmokeProgram.ThermalExternalDriftConfirmation(modelNumber));
    }

    [Fact]
    public void ParseExpectedFirmwareChanges_AcceptsAnySupportedCombination()
    {
        IReadOnlySet<FirmwareComponent> result =
            FirmwareSmokeProgram.ParseExpectedFirmwareChanges("bios, EC,mcu,VBIOS");

        Assert.Equal(
            new HashSet<FirmwareComponent>(Enum.GetValues<FirmwareComponent>()),
            result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("touchpad")]
    [InlineData("BIOS,touchpad")]
    public void ParseExpectedFirmwareChanges_RejectsMissingOrUnknownComponents(
        string value)
    {
        Assert.Throws<ArgumentException>(() =>
            FirmwareSmokeProgram.ParseExpectedFirmwareChanges(value));
    }

    [Fact]
    public void FirmwareUpdateScriptContainsNoModelOrVersionSpecificGate()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "Test-FirmwareUpdate.ps1"));

        Assert.DoesNotContain("RZ09-", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-MCU-SMOKE", script, StringComparison.OrdinalIgnoreCase);

        int executionStart = script.IndexOf(
            "Push-Location $repositoryRoot",
            StringComparison.Ordinal);
        Assert.True(executionStart >= 0);
        string parameterAndValidationBlock = script[..executionStart];
        Assert.DoesNotMatch(
            FirmwareVersionLiteralPattern,
            parameterAndValidationBlock);
    }

    [Theory]
    [InlineData("throw 'BIOS 4.00 is required'")]
    [InlineData("if ($bios -lt 4.00) { }")]
    [InlineData("# requires BIOS 4.00")]
    [InlineData("$min = '4.00'")]
    public void FirmwareVersionLiteralGuardRejectsHardcodedVersions(string line)
    {
        Assert.Matches(FirmwareVersionLiteralPattern, line);
    }

    [Fact]
    public void FirmwareUpdateScriptDelegatesComponentValidationToTheTool()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "Test-FirmwareUpdate.ps1"));

        Assert.DoesNotContain("allowedComponents", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsupportedComponents", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "@('PostUpdate', 'Rehearsal', 'Reversible')",
            script,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private const string FirmwareVersionLiteralPattern =
        @"(?<![A-Za-z0-9])v?\d+\.\d+(?:\.\d+)*(?![A-Za-z0-9])";
}
