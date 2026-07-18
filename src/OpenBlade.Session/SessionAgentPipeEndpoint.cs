using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OpenBlade.Session;

public sealed record SessionAgentPipeEndpoint(string PipeName)
{
    public static SessionAgentPipeEndpoint Create(SecurityIdentifier userSid)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(userSid.Value));
        return new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.{Convert.ToHexString(hash.AsSpan(0, 16))}");
    }
}
