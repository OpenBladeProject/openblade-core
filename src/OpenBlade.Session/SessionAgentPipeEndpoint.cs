using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OpenBlade.Session;

public sealed record SessionAgentPipeEndpoint(string PipeName)
{
    public static SessionAgentPipeEndpoint Create(
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
        return new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.{Convert.ToHexString(hash.AsSpan(0, 16))}");
    }
}
