using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace OpenBlade.Installer.UI.Tests;

public sealed class InstallerProgressPipeTests
{
    [Fact]
    public void AdministratorsCanReadAndWriteProgressPipe()
    {
        SecurityIdentifier currentUser = new(WellKnownSidType.BuiltinUsersSid, null);
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);

        PipeAccessRule rule = InstallerProgressPipe.CreateSecurity(currentUser)
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Single(candidate => candidate.IdentityReference == administrators);

        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(
            PipeAccessRights.ReadWrite,
            rule.PipeAccessRights & PipeAccessRights.ReadWrite);
    }

    [Fact]
    public async Task ReaderBoundsIndividualMessagesAndContinuesAtNextLine()
    {
        List<InstallerSetupProgress> messages = new();
        using StringReader reader = new(
            new string('x', 2000) + "\r\nv1|InspectPayload");

        await InstallerProgressPipe.ReadMessagesAsync(reader, messages.Add);

        InstallerSetupProgress message = Assert.Single(messages);
        Assert.Equal(InstallerSetupProgressCode.InspectPayload, message.Code);
    }

    [Fact]
    public async Task ReaderDecodesBoundedCopyFileArgument()
    {
        List<InstallerSetupProgress> messages = new();
        using StringReader reader = new(
            "v1|CopyFile|cGF5bG9hZFxPcGVuQmxhZGUuVHJheS5leGU=\r\n");

        await InstallerProgressPipe.ReadMessagesAsync(reader, messages.Add);

        InstallerSetupProgress message = Assert.Single(messages);
        Assert.Equal(InstallerSetupProgressCode.CopyFile, message.Code);
        Assert.Equal(@"payload\OpenBlade.Tray.exe", message.Argument);
    }

    [Fact]
    public async Task ReaderAcceptsMaximumNonAsciiProducerArgument()
    {
        string argument = new('界', 512);
        string wireValue = "v1|CopyFile|"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(argument));
        Assert.True(wireValue.Length > 1024);
        List<InstallerSetupProgress> messages = new();
        using StringReader reader = new(wireValue + "\r\n");

        await InstallerProgressPipe.ReadMessagesAsync(reader, messages.Add);

        InstallerSetupProgress message = Assert.Single(messages);
        Assert.Equal(InstallerSetupProgressCode.CopyFile, message.Code);
        Assert.Equal(argument, message.Argument);
    }

    [Theory]
    [InlineData("v2|InspectPayload")]
    [InlineData("v1|Unknown")]
    [InlineData("v1|InspectPayload|extra")]
    [InlineData("v1|CopyFile")]
    [InlineData("v1|CopyFile|not-base64")]
    [InlineData("v1|CopyFile|DQpmb3JnZWQ=")]
    public void ParserRejectsUnknownOrMalformedMessages(string value)
    {
        Assert.False(InstallerSetupProgress.TryParse(value, out _));
    }
}
