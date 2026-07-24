using System.Security.Principal;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAgentEventEndpointTests
{
    [Fact]
    public void Create_IsStableForTheSameUser()
    {
        var sid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");

        SessionAgentEventEndpoint first = SessionAgentEventEndpoint.Create(sid);
        SessionAgentEventEndpoint second = SessionAgentEventEndpoint.Create(sid);

        Assert.Equal(first, second);
        Assert.StartsWith(
            "OpenBlade.SessionAgent.DisplayBrightnessChanged.",
            first.DisplayBrightnessChangedMessageName,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Create_IsIsolatedByUser()
    {
        SessionAgentEventEndpoint first = SessionAgentEventEndpoint.Create(
            new SecurityIdentifier("S-1-5-21-1-2-3-1001"));
        SessionAgentEventEndpoint second = SessionAgentEventEndpoint.Create(
            new SecurityIdentifier("S-1-5-21-1-2-3-1002"));

        Assert.NotEqual(
            first.DisplayBrightnessChangedMessageName,
            second.DisplayBrightnessChangedMessageName);
    }
}
