#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Text.Json;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup.Tests;

public sealed class ScratchReceiverClosureParityOfflineTests
{
    [Fact]
    public void NativeReceiptSerializerPreservesExistingReceiptMeaningAndBytes()
    {
        var role = new WindowsNativePackageRoleProof.RoleImage((WindowsNativeRole)0,
            "payload", "payload/image.exe", "member", "image.exe", 123, new string('a', 64));
        var value = new ScratchReceiptRecord(2, "SetupPayloadStarting", Guid.Empty, Guid.Empty,
            Guid.Empty, DateTimeOffset.UnixEpoch, "package", 123, "0.0.0.0", "source", "manifest",
            "enrollment", "broker", "key", 4, role, role);
        byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(new
        {
            value.schema,
            value.phase,
            value.scratchId,
            value.installationId,
            value.requestId,
            value.issuedAt,
            value.packageSha256,
            value.packageLength,
            value.version,
            value.sourceRevision,
            value.manifestSha256,
            value.enrollmentSha256,
            value.brokerBindingSha256,
            value.signingKeyId,
            value.sequence,
            value.service,
            value.sessionAgent,
        });
        Assert.Equal(legacy, JsonSerializer.SerializeToUtf8Bytes(value, ScratchReceiptJsonContext.Default.ScratchReceiptRecord));
    }

    [Fact]
    public async Task NativeReadinessSerializerPreservesExistingFrames()
    {
        var value = new WindowsScratchCandidateReadiness.StartupFrame("verifyProvisional", 79,
            Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 152));
        using var native = new MemoryStream();
        using var legacy = new MemoryStream();
        await WindowsScratchCandidateReadiness.WriteFrameAsync(native, value, default);
        await OpenBlade.Contracts.IpcMessageCodec.WriteFramedAsync(legacy, value, default);
        Assert.Equal(legacy.ToArray(), native.ToArray());
    }

    [Fact]
    public void NativeImageAndClosureMatchSharedRustVectorAndRefusals()
    {
        using var v = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "scratch-native-receiver-v2.json")));
        Assert.Equal(ScratchCandidateLaunch.EvidenceName, v.RootElement.GetProperty("launchEvidenceName").GetString());
        byte[] image = ScratchCandidateLaunchOfflineTests.VectorImage();
        Assert.True(ScratchReceiverClosure.AcceptNativeImage(image));
        for (int length = 0; length < image.Length; length++)
        {
            Assert.False(ScratchReceiverClosure.AcceptNativeImage(image[..length]));
        }
        foreach (int offset in new[] { 0, 60, 132, 150, 152, 264, 368, 496 })
        {
            byte[] changed = image.ToArray(); changed[offset] ^= 0xff;
            Assert.False(ScratchReceiverClosure.AcceptNativeImage(changed));
        }
        using var files = new ScratchCandidateLaunchOfflineTests.NativeFiles();
        using var closure = ScratchReceiverClosure.Retain(files.Storage);
        Assert.Equal(v.RootElement.GetProperty("canonicalSha256").GetString(), Convert.ToHexStringLower(closure.RequireUnchanged()));
    }

    [Fact]
    public void CompiledNativeReceiverIsAcceptedAsDataWithoutExecution()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ScratchCandidateLaunch.ReceiverName);
        using var image = File.OpenRead(path);
        ScratchReceiverClosure.RequireNativeImage(image);
    }
}
#endif
