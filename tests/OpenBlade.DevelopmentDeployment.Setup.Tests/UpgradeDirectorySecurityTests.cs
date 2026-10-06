using System.Security.AccessControl;
using OpenBlade.DevelopmentDeployment.Setup;

namespace OpenBlade.DevelopmentDeployment.Setup.Tests;

public sealed class UpgradeDirectorySecurityTests
{
    private const string User = "S-1-5-21-1-2-3-1001";
    private const string FullControl = "(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    private const string ReadExecute = "(A;OICI;0x1200a9;;;" + User + ")";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AcceptsExpectedAndPersistedBrokerAndClientDescriptors(bool client, bool autoInherited)
    {
        // The group SID in the observed installed descriptor is outside the
        // original owner/DACL admission boundary and remains excluded.
        string sddl = "O:BAG:" + User + "D:P" + (autoInherited ? "AI" : "")
            + FullControl + (client ? ReadExecute : "");
        DevelopmentDeploymentProvisioner.RequireUpgradeDirectorySecurity(Descriptor(sddl), client ? User : null);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("unprotected")]
    [InlineData("autoInheritRequired")]
    [InlineData("missingAce")]
    [InlineData("extraAce")]
    [InlineData("write")]
    [InlineData("genericWrite")]
    [InlineData("genericAll")]
    [InlineData("deny")]
    [InlineData("inheritance")]
    [InlineData("propagation")]
    [InlineData("inheritedAce")]
    [InlineData("order")]
    public void RejectsEverySubstantiveDifferenceEvenWithAutoInheritedMarker(string difference)
    {
        string sddl = "O:BAD:PAI" + FullControl + ReadExecute;
        sddl = difference switch
        {
            "owner" => sddl.Replace("O:BA", "O:SY", StringComparison.Ordinal),
            "unprotected" => sddl.Replace("D:PAI", "D:AI", StringComparison.Ordinal),
            "autoInheritRequired" => sddl.Replace("D:PAI", "D:PARAI", StringComparison.Ordinal),
            "missingAce" => sddl.Replace("(A;OICI;FA;;;SY)", "", StringComparison.Ordinal),
            "extraAce" => sddl + "(A;OICI;FR;;;WD)",
            "write" => sddl.Replace("0x1200a9", "FW", StringComparison.Ordinal),
            "genericWrite" => sddl.Replace("0x1200a9", "GW", StringComparison.Ordinal),
            "genericAll" => sddl.Replace("0x1200a9", "GA", StringComparison.Ordinal),
            "deny" => sddl.Replace(ReadExecute, ReadExecute.Replace("(A;", "(D;", StringComparison.Ordinal), StringComparison.Ordinal),
            "inheritance" => sddl.Replace(ReadExecute, ReadExecute.Replace("OICI", "OI", StringComparison.Ordinal), StringComparison.Ordinal),
            "propagation" => sddl.Replace(ReadExecute, ReadExecute.Replace("OICI", "OICINP", StringComparison.Ordinal), StringComparison.Ordinal),
            "inheritedAce" => sddl.Replace(ReadExecute, ReadExecute.Replace("OICI", "OICIID", StringComparison.Ordinal), StringComparison.Ordinal),
            "order" => "O:BAD:PAI(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)" + ReadExecute,
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };
        if (difference is "autoInheritRequired" or "order")
        {
            // DirectorySecurity normalizes these before comparison, as it did
            // before this fix. The comparison itself must not erase either one.
            Assert.Throws<UnauthorizedAccessException>(() => DevelopmentDeploymentProvisioner.RequireUpgradeDirectorySecurity(
                new RawSecurityDescriptor(sddl), User));
        }
        else
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                DevelopmentDeploymentProvisioner.RequireUpgradeDirectorySecurity(Descriptor(sddl), User));
        }
    }

    [Theory]
    [InlineData(ControlFlags.OwnerDefaulted)]
    [InlineData(ControlFlags.DiscretionaryAclDefaulted)]
    public void RawComparisonDoesNotIgnoreOtherControlFlags(ControlFlags otherFlag)
    {
        RawSecurityDescriptor raw = new("O:BAD:PAI" + FullControl);
        raw.SetFlags(raw.ControlFlags | otherFlag);
        Assert.Throws<UnauthorizedAccessException>(() =>
            DevelopmentDeploymentProvisioner.RequireUpgradeDirectorySecurity(raw, null));
    }

    [Fact]
    public void RejectsNullDacl()
    {
        Assert.Throws<UnauthorizedAccessException>(() => DevelopmentDeploymentProvisioner.RequireUpgradeDirectorySecurity(
            Descriptor("O:BAD:PNO_ACCESS_CONTROL"), null));
    }

    [Fact]
    public void PreservesDaclRevisionDuringComparison()
    {
        DirectorySecurity security = Descriptor("O:BAD:PAI" + FullControl);
        RawSecurityDescriptor raw = new(security.GetSecurityDescriptorBinaryForm(), 0);
        RawAcl revised = new(GenericAcl.AclRevisionDS, raw.DiscretionaryAcl!.Count);
        for (int index = 0; index < raw.DiscretionaryAcl.Count; index++)
        { revised.InsertAce(index, raw.DiscretionaryAcl[index]); }
        raw.DiscretionaryAcl = revised;
        byte[] bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
        security.SetSecurityDescriptorBinaryForm(bytes);
        Assert.Throws<UnauthorizedAccessException>(() =>
            DevelopmentDeploymentProvisioner.RequireUpgradeDirectorySecurity(security, null));
    }

    private static DirectorySecurity Descriptor(string sddl)
    {
        DirectorySecurity security = new();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }
}
