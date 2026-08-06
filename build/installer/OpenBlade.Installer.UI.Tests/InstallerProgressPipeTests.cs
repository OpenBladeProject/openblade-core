using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
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
        List<string> messages = new();
        using StringReader reader = new(new string('x', 2000) + "\r\nnext");

        await InstallerProgressPipe.ReadMessagesAsync(reader, messages.Add);

        Assert.Collection(
            messages,
            message => Assert.Equal(512, message.Length),
            message => Assert.Equal("next", message));
    }
}
