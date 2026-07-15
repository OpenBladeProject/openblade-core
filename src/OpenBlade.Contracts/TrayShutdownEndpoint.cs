using System.Security.Cryptography;
using System.Text;

namespace OpenBlade.Contracts;

public static class TrayShutdownEndpoint
{
    public static string ForExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("Tray executable path must be fully qualified.", nameof(executablePath));
        }

        string normalized = Path.GetFullPath(executablePath).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $@"Local\OpenBlade.Tray.Shutdown.{Convert.ToHexString(hash.AsSpan(0, 16))}";
    }
}
