using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class PipeSecurityFactoryTests
{
    [Fact]
    public void Create_AllowsLocalSystemAndConfiguredUserOnly()
    {
        SecurityIdentifier user = new("S-1-5-21-1-2-3-1001");

        PipeSecurity security = PipeSecurityFactory.Create(user);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));
        PipeAccessRule[] pipeRules = rules.Cast<PipeAccessRule>().ToArray();

        Assert.Equal(2, pipeRules.Length);
        Assert.Contains(pipeRules, rule => rule.IdentityReference == user);
        Assert.Contains(pipeRules, rule =>
            rule.IdentityReference == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Assert.All(pipeRules, rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));
        Assert.Equal(
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            pipeRules.Single(rule => rule.IdentityReference == user).PipeAccessRights);
        Assert.Equal(
            PipeAccessRights.FullControl,
            pipeRules.Single(rule =>
                rule.IdentityReference == new SecurityIdentifier(
                    WellKnownSidType.LocalSystemSid,
                    null)).PipeAccessRights);
        Assert.True(security.AreAccessRulesProtected);
        Assert.DoesNotContain(pipeRules, rule =>
            rule.IdentityReference == new SecurityIdentifier(
                WellKnownSidType.BuiltinUsersSid,
                null));
    }
}
