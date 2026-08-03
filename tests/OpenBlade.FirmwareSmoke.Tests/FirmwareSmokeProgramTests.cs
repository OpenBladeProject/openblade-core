using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;

namespace OpenBlade.FirmwareSmoke.Tests;

public sealed class FirmwareSmokeProgramTests
{
    [Theory]
    [InlineData("RZ09-0581", "RZ09-0581-MCU-SMOKE")]
    [InlineData("RZ09-0528", "RZ09-0528-MCU-SMOKE")]
    [InlineData("rz09-05289en4", "RZ09-0528-MCU-SMOKE")]
    public void TargetConfirmation_UsesCanonicalExactModel(
        string modelNumber,
        string expected)
    {
        Assert.Equal(expected, FirmwareSmokeProgram.TargetConfirmation(modelNumber));
    }

    [Fact]
    public void TargetConfirmationFormat_DocumentsModelSpecificToken()
    {
        Assert.Equal("<MODEL>-MCU-SMOKE", FirmwareSmokeProgram.TargetConfirmationFormat);
        Assert.Equal(
            "RZ09-0528-MCU-SMOKE",
            FirmwareSmokeProgram.TargetConfirmation(
                RazerBladeModelCatalog.Rz090528ModelNumber));
    }
}
