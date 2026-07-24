using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OpenBlade.Session;

public sealed record SessionAgentEventEndpoint(
    string DisplayBrightnessChangedMessageName)
{
    private const string DisplayBrightnessChangedPrefix =
        "OpenBlade.SessionAgent.DisplayBrightnessChanged";

    public static SessionAgentEventEndpoint Create(SecurityIdentifier userSid)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(userSid.Value));
        return new SessionAgentEventEndpoint(
            $"{DisplayBrightnessChangedPrefix}."
                + Convert.ToHexString(hash.AsSpan(0, 16)));
    }
}
