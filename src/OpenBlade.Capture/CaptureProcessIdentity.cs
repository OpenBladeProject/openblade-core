using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace OpenBlade.Capture;

internal readonly record struct CaptureProcessIdentity(
    int ProcessId,
    long StartUtcTicks,
    string BuildVersion,
    string ExecutableSha256)
{
    internal static CaptureProcessIdentity Current()
    {
        using Process process = Process.GetCurrentProcess();
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The current Capture executable path is unavailable.");
        string version = typeof(CaptureProcessIdentity)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? throw new InvalidOperationException(
                "The current Capture build version is unavailable.");
        using FileStream stream = new(
            executablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return new CaptureProcessIdentity(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            version,
            Convert.ToHexString(SHA256.HashData(stream)));
    }
}
