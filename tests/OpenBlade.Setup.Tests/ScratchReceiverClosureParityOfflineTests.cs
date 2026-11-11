#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup.Tests;

// Files and current-token scratch ownership only. No processes or IPC.
public sealed class ScratchReceiverClosureParityOfflineTests
{
    [Fact]
    public void RealCSharpClosureEncoderMatchesSharedRustKnownAnswer()
    {
        using var vector = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "scratch-receiver-closure-v1.json")));
        Assert.Equal(ScratchCandidateLaunch.EvidenceName,
            vector.RootElement.GetProperty("launchEvidenceName").GetString());
        var storage = WindowsCandidateReceiptStorage.CreateScratch();
        string receiver = Path.GetDirectoryName(ScratchCandidateLaunch.ReceiverPath(storage))!;
        try
        {
            foreach (var member in vector.RootElement.GetProperty("members").EnumerateObject())
            {
                string path = Path.Combine(receiver, member.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, member.Value.GetString()!, new UTF8Encoding(false));
            }
            byte[] expected = Convert.FromHexString(vector.RootElement.GetProperty("canonicalSha256").GetString()!);
            using (var closure = ScratchReceiverClosure.Retain(storage))
            {
                byte[] actual = closure.RequireUnchanged();
                Assert.Equal(expected, actual);
                Assert.NotEqual(SHA256.HashData(File.ReadAllBytes(
                    Path.Combine(receiver, ScratchCandidateLaunch.ReceiverName))), actual);
                Assert.Equal(actual, closure.RequireUnchanged());
            }
            // The real encoder seals every declared namespace and releases all
            // member/namespace handles, including resource and RID ancestors.
            foreach (string path in Directory.EnumerateFiles(receiver, "*", SearchOption.AllDirectories))
            {
                using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            File.WriteAllText(Path.Combine(receiver, "released"), "released");
        }
        finally
        {
            Directory.Delete(storage.Root, recursive: true);
        }
    }
}
#endif
