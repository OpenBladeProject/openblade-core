using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OpenBlade.Session;

public sealed record SessionAgentEventEndpoint(
    string DisplayBrightnessChangedMessageName,
    string PerformanceShortcutNotificationMessageName)
{
    private const string DisplayBrightnessChangedPrefix =
        "OpenBlade.SessionAgent.DisplayBrightnessChanged";
    private const string PerformanceShortcutNotificationPrefix =
        "OpenBlade.SessionAgent.PerformanceShortcutNotification";

    public static SessionAgentEventEndpoint Create(
        SecurityIdentifier userSid,
        int sessionId)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{userSid.Value}|{sessionId}"));
        string suffix = Convert.ToHexString(hash.AsSpan(0, 16));
        return new SessionAgentEventEndpoint(
            $"{DisplayBrightnessChangedPrefix}.{suffix}",
            $"{PerformanceShortcutNotificationPrefix}.{suffix}");
    }
}
