#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Deployment;

// Canonical records for the offline rehearsal. Claim consumes the pending name.
[SupportedOSPlatform("windows")]
internal sealed class WindowsCandidatePackageReceipt
{
    internal const string PendingName = "candidate-package.pending.json";
    internal const string ClaimedName = "candidate-package.claimed.json";
    internal const string StartupName = "candidate-package.startup.json";
    internal const string CommittedName = "candidate-package.committed.json";

    private readonly WindowsCandidateReceiptStorage storage;
    private readonly string staging;
    private readonly WindowsNativePackageRoleProof proof;
    private readonly byte[] pending;
    private readonly byte[] startup;
    private readonly byte[] committed;
    private readonly byte[] activeState;
    private readonly Guid installationId;
    private readonly Guid requestId;

    internal Guid InstallationId => installationId;
    internal Guid RequestId => requestId;

    private WindowsCandidatePackageReceipt(WindowsCandidateReceiptStorage storage,
        WindowsNativePackageRoleProof proof, OpenBladeVerifiedRelease release,
        string manifestSha256, string enrollmentSha256, byte[] issuedState, byte[] activeState,
        Guid requestId, DateTimeOffset issuedAt)
    {
        var manifest = release.Manifest;
        this.storage = storage;
        this.proof = proof;
        this.requestId = requestId;
        this.activeState = activeState;
        installationId = manifest.InstallationId;
        staging = Path.Combine(storage.Root, "staging", requestId.ToString("N"));
        string brokerBindingSha256 = Convert.ToHexStringLower(SHA256.HashData(issuedState));
        pending = Encode("BrokerVerified");
        startup = Encode("SetupPayloadStarting");
        committed = Encode("SetupPayloadCommitted");

        byte[] Encode(string phase) => JsonSerializer.SerializeToUtf8Bytes(new ScratchReceiptRecord(
            2, phase, storage.ScratchId, installationId, requestId, issuedAt,
            proof.Identity.PackageSha256, manifest.InstallerSize, manifest.Version, manifest.SourceRevision,
            manifestSha256, enrollmentSha256, brokerBindingSha256, release.SigningKeyId, manifest.Sequence,
            proof.Service, proof.SessionAgent), ScratchReceiptJsonContext.Default.ScratchReceiptRecord);
    }

    internal static WindowsCandidatePackageReceipt Publish(WindowsCandidateReceiptStorage storage,
        WindowsNativePackageRoleProof proof, OpenBladeVerifiedRelease release,
        byte[] verifiedManifest, byte[] enrollment, byte[] issuedState, Guid requestId, DateTimeOffset issuedAt)
    {
        var receipt = Create(storage, proof, release, verifiedManifest, enrollment, issuedState, issuedState,
            requestId, issuedAt);
        using FileStream binding = receipt.OpenActiveBinding();
        using var directory = storage.PinDirectory(receipt.staging);
        WindowsCandidateReceiptStorage.RequireAbsent(Path.Combine(receipt.staging, ClaimedName));
        storage.Publish(Path.Combine(receipt.staging, PendingName), receipt.pending);
        return receipt;
    }

    internal static WindowsCandidatePackageReceipt Reopen(WindowsCandidateReceiptStorage storage,
        WindowsNativePackageRoleProof proof, OpenBladeVerifiedRelease release, byte[] verifiedManifest,
        byte[] enrollment, byte[] issuedState, byte[] activeState, Guid requestId, DateTimeOffset issuedAt,
        ReadOnlySpan<byte> record)
    {
        var receipt = Create(storage, proof, release, verifiedManifest, enrollment, issuedState, activeState,
            requestId, issuedAt);
        if (!record.SequenceEqual(receipt.pending))
        {
            throw new InvalidDataException("The pending candidate receipt differs from the independently verified binding.");
        }
        return receipt;
    }

    internal static DateTimeOffset ReadIssuedAt(byte[] record)
    {
        try
        {
            using var document = JsonDocument.Parse(record);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("issuedAt", out var value)
                && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var timestamp)
                && timestamp.Offset == TimeSpan.Zero)
            {
                return timestamp;
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The pending candidate receipt is malformed.", error);
        }
        throw new InvalidDataException("The pending candidate receipt has an invalid issuance timestamp.");
    }

    private static WindowsCandidatePackageReceipt Create(WindowsCandidateReceiptStorage storage,
        WindowsNativePackageRoleProof proof, OpenBladeVerifiedRelease release, byte[] verifiedManifest,
        byte[] enrollment, byte[] issuedState, byte[] activeState, Guid requestId, DateTimeOffset issuedAt)
    {
        var manifest = release.Manifest;
        if (requestId == Guid.Empty || manifest.InstallationId == Guid.Empty
            || proof.Identity != new WindowsNativePayloadIdentity(manifest.InstallerSha256,
                new(manifest.SourceRevision, Version.Parse(manifest.Version))))
        {
            throw new InvalidDataException("The candidate role proof and signed release binding differ.");
        }

        return new(storage, proof, release, Convert.ToHexStringLower(SHA256.HashData(verifiedManifest)),
            Convert.ToHexStringLower(SHA256.HashData(enrollment)), issuedState, activeState, requestId, issuedAt);
    }

    internal void RequireBinding(Guid installation, Guid request, string programFilesRoot)
    {
        if (installation != installationId || request != requestId
            || programFilesRoot != Path.Combine(storage.Root, "program-files"))
        {
            throw new InvalidDataException("Candidate Setup does not match the issued installation and request.");
        }
    }

    internal WindowsCandidateReceiptStorage.DirectoryLease PinSourcePayload(string payload)
    {
        if (payload != Path.Combine(storage.Root, "payload"))
        {
            throw new UnauthorizedAccessException("Candidate Setup requires its fixed scratch source payload.");
        }
        return storage.PinDirectory(payload);
    }

    internal WindowsCandidateReceiptStorage.DirectoryLease PinProgramFiles() =>
        storage.PinDirectory(Path.Combine(storage.Root, "program-files"));

    internal void Claim(string stagedPayload, CancellationToken cancellationToken)
    {
        using var requestDirectory = storage.PinDirectory(staging);
        using FileStream binding = OpenActiveBinding();
        using var payloadDirectory = storage.PinDirectory(stagedPayload);
        WindowsCandidateReceiptStorage.RequireAbsent(Path.Combine(stagedPayload, StartupName));
        WindowsCandidateReceiptStorage.RequireAbsent(Path.Combine(stagedPayload, CommittedName));
        string pendingPath = Path.Combine(staging, PendingName);
        string claimedPath = Path.Combine(staging, ClaimedName);
        WindowsCandidateReceiptStorage.RequireAbsent(claimedPath);
        using FileStream service = OpenRole(stagedPayload, proof.Service, cancellationToken);
        using FileStream agent = OpenRole(stagedPayload, proof.SessionAgent, cancellationToken);
        using (FileStream record = storage.OpenFile(pendingPath))
        {
            WindowsCandidateReceiptStorage.RequireExact(record, pending);
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(pendingPath, claimedPath, overwrite: false);
        using FileStream claimed = storage.OpenFile(claimedPath);
        WindowsCandidateReceiptStorage.RequireExact(claimed, pending);
    }

    internal void VerifyClaimedPayload(string payload, CancellationToken cancellationToken, Action? onVerified = null)
    {
        using var requestDirectory = storage.PinDirectory(staging);
        using FileStream binding = OpenActiveBinding();
        using var payloadDirectory = storage.PinDirectory(payload);
        WindowsCandidateReceiptStorage.RequireAbsent(Path.Combine(staging, PendingName));
        using FileStream claimed = storage.OpenFile(Path.Combine(staging, ClaimedName));
        WindowsCandidateReceiptStorage.RequireExact(claimed, pending);
        using FileStream service = OpenRole(payload, proof.Service, cancellationToken);
        using FileStream agent = OpenRole(payload, proof.SessionAgent, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        onVerified?.Invoke();
    }

    internal void PublishStartup(string installedPayload, CancellationToken cancellationToken) =>
        VerifyClaimedPayload(installedPayload, cancellationToken,
            () =>
            {
                WindowsCandidateReceiptStorage.RequireAbsent(Path.Combine(installedPayload, CommittedName));
                storage.Publish(Path.Combine(installedPayload, StartupName), startup);
            });

    // The second digest binds the current launch, which can differ from issuance.
    internal byte[] StartupCommitment(ReadOnlySpan<byte> setupIdentity)
    {
        byte[] startupDigest = SHA256.HashData(startup);
        using var binding = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        binding.AppendData(activeState);
        binding.AppendData(setupIdentity);
        binding.AppendData(startupDigest);
        return [.. startupDigest, .. binding.GetHashAndReset()];
    }

    internal void PublishCommitted(string installedPayload, CancellationToken cancellationToken,
        Action? verifyReadinessAtPublication = null) =>
        VerifyClaimedPayload(installedPayload, cancellationToken,
            () =>
            {
                string destination = Path.Combine(installedPayload, CommittedName);
                WindowsCandidateReceiptStorage.RequireAbsent(destination);
                storage.Retire(Path.Combine(installedPayload, StartupName), startup);
                cancellationToken.ThrowIfCancellationRequested();
                // The verified rename is the point of no return. A failed
                // publish remains rollbackable and cannot be retried as startup.
                storage.Publish(destination, committed, verifyReadinessAtPublication);
            });

    internal void VerifyCommitted(string installedPayload, CancellationToken cancellationToken, Action? onVerified = null)
    {
        using var payloadDirectory = storage.PinDirectory(installedPayload);
        using FileStream record = storage.OpenFile(Path.Combine(installedPayload, CommittedName));
        WindowsCandidateReceiptStorage.RequireExact(record, committed);
        using FileStream service = OpenRole(installedPayload, proof.Service, cancellationToken);
        using FileStream agent = OpenRole(installedPayload, proof.SessionAgent, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        onVerified?.Invoke();
    }

    internal FileStream OpenActiveBinding()
    {
        // Claim and commit hold the in-flight binding against replacement or deletion.
        // The caller already holds the scratch root, directly or via its request tree.
        FileStream file = storage.OpenFile(Path.Combine(storage.Root, "unresolved-deployment.json"));
        try
        {
            WindowsCandidateReceiptStorage.RequireExact(file, activeState);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private FileStream OpenRole(string directory, WindowsNativePackageRoleProof.RoleImage role,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream = storage.OpenFile(Path.Combine(directory, role.InstalledName));
        try
        {
            if (stream.Length != role.Length || role.Length is <= 0 or > WindowsNativeRoleInventory.MaximumRoleImageBytes)
            {
                throw new InvalidDataException($"Candidate {role.Role} length differs from the authenticated package.");
            }
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> buffer = stackalloc byte[16 * 1024];
            int remaining = role.Length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<byte> chunk = buffer[..Math.Min(remaining, buffer.Length)];
                stream.ReadExactly(chunk);
                hash.AppendData(chunk);
                remaining -= chunk.Length;
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(role.Sha256)))
            {
                throw new InvalidDataException($"Candidate {role.Role} digest differs from the authenticated package.");
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
internal sealed record ScratchReceiptRecord(int schema, string phase, Guid scratchId, Guid installationId,
    Guid requestId, DateTimeOffset issuedAt, string packageSha256, long packageLength, string version,
    string sourceRevision, string manifestSha256, string enrollmentSha256, string brokerBindingSha256,
    string signingKeyId, long sequence, WindowsNativePackageRoleProof.RoleImage service,
    WindowsNativePackageRoleProof.RoleImage sessionAgent);

[JsonSerializable(typeof(ScratchReceiptRecord))]
internal partial class ScratchReceiptJsonContext : JsonSerializerContext;
#endif
