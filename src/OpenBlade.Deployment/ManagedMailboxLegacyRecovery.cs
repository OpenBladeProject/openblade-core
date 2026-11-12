using System.Runtime.Versioning;

namespace OpenBlade.Deployment;

// Legacy records remain in their original namespace and are never converted to
// a new request or mapped to a new epoch. This separate terminal envelope binds
// the old bytes to the exact reviewed historical Setup contract.
[SupportedOSPlatform("windows")]
internal static class ManagedMailboxLegacyRecovery
{
    internal const string ExitBeforeBindingSource = "ffc20060aa9a8953d7aba44837a0f02dc09c9295";
    private const string ExitBeforeBindingTree = "e7531d11d9cf8bc24ddde28b8d6ff41d98034a06";
    internal static void Resolve(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody native,
        Action<ManagedMailboxRun, ManagedMailboxBuildBinding, byte[]>? authenticate = null, bool terminalOnly = false)
    {
        byte[] runBytes = files.ReadCurrent() ?? throw new IOException("Legacy original managed run is missing.");
        var run = ManagedMailboxRun.Parse(runBytes);
        byte[] requestBytes = ManagedMailboxRecovery.Read(files, Path.Combine(files.Root, "request.bin"));
        var request = ManagedMailboxRequest.Parse(requestBytes);
        byte[] receipt = ManagedMailboxRecovery.Read(files, Path.Combine(files.Root, "receipt.bin"));
        byte[] mapping = ManagedMailboxRecovery.Read(files, Path.Combine(files.Root, "mapping.bin"));
        byte[] bindingBytes = ManagedMailboxRecovery.Read(files, Path.Combine(files.Root, "run-binding-" + run.Run + ".bin"));
        var binding = ManagedMailboxBuildBinding.Parse(bindingBytes);
        if (!ManagedMailboxRecovery.Read(files, Path.Combine(files.Root, "run-" + run.Run + ".bin")).AsSpan().SequenceEqual(runBytes)
            || request.RunDigest != NativeCanonical.Digest(runBytes) || binding.RunDigest != request.RunDigest
            || binding.Installation != request.Installation
            || !receipt.AsSpan().SequenceEqual(NativeCanonical.Lines("OpenBlade.ManagedMailboxQuiescent.v1", NativeCanonical.Digest(requestBytes),
                Convert.ToBase64String(runBytes), "transport-quiescent-no-mutation")))
        { throw new IOException("Legacy original run, signed binding or receipt differs."); }
        var original = native.RequireLegacyBinding(mapping);
        byte[] expectedMapping = NativeCanonical.Lines("OpenBlade.ManagedMailboxEpoch.v1", NativeCanonical.Digest(receipt), NativeCanonical.Digest(requestBytes),
            original.Epoch.Epoch, request.Installation, request.Request, request.Package);
        if (!mapping.AsSpan().SequenceEqual(expectedMapping) || original.Source != ExitBeforeBindingSource
            || binding.Enrollment != NativeCanonical.Digest(original.Projection))
        { throw new IOException("Legacy original-exit contract is not established for this signed source/enrollment."); }
        // In THIS pinned Setup, the retained managed original must signal exit
        // before ConfirmOriginalExited can populate the receipt subsequently used
        // by BindEpoch. This is positive historical process-contract evidence,
        // not a new process-exit observation or an absent-PID/SCM certificate.
        byte[] attestation = NativeCanonical.Lines("OpenBlade.LegacyOriginalExitContract.v2", run.Process, NativeCanonical.Digest(runBytes),
            NativeCanonical.Digest(requestBytes), NativeCanonical.Digest(receipt), NativeCanonical.Digest(mapping), ExitBeforeBindingSource, ExitBeforeBindingTree);
        if (authenticate is null)
        {
            using var package = WindowsManagedMailboxPackage.OpenCachedHistorical(files, binding.Package, original.Projection);
            if (package.Installation != binding.Installation || package.EnrollmentDigest != binding.Enrollment)
            { throw new IOException("Legacy signed managed predecessor differs."); }
        }
        else { authenticate(run, binding, original.Projection); }
        var transfer = new ManagedMailboxSelection(1, new string('0', 64), runBytes, requestBytes);
        var fresh = native.Resolve(transfer, original.Epoch);
        string proofPath = Path.Combine(files.Root, "legacy-native-proof-v2.bin");
        byte[] proof = ManagedMailboxRecovery.ReadOptional(files, proofPath) ?? fresh.Proof;
        var fields = NativeCanonical.ParseLines(proof, "OpenBlade.ManagedNativeResolution.v2", 7);
        if (fields[0] != NativeCanonical.Digest(transfer.Encode()) || fields[1] != fresh.Disposition || fields[2] != original.Epoch.Epoch
            || fields[3] != NativeCanonical.Digest(Convert.FromBase64String(fields[5])) || !NativeCanonical.IsHex(fields[4], 64))
        { throw new IOException("Legacy terminal proof is unmatched."); }
        native.RequirePreservedResolution(transfer, original.Epoch, proof, fresh.Disposition);
        byte[] terminal = NativeCanonical.Lines("OpenBlade.ManagedMailboxLegacyTerminal.v2", NativeCanonical.Digest(runBytes), NativeCanonical.Digest(requestBytes),
            NativeCanonical.Digest(receipt), NativeCanonical.Digest(mapping), NativeCanonical.Digest(bindingBytes), NativeCanonical.Digest(attestation), fresh.Disposition, NativeCanonical.Digest(proof));
        void RequireOrPublish(string path, byte[] bytes)
        {
            if (!terminalOnly) { ManagedMailboxRecovery.ExactOrPublish(files, path, bytes); }
            else if (!ManagedMailboxRecovery.Read(files, path).AsSpan().SequenceEqual(bytes))
            { throw new IOException("Legacy terminal is incomplete or differs; no pre-stop recovery was admitted."); }
        }
        RequireOrPublish(Path.Combine(files.Root, "legacy-original-exit-contract-v2.bin"), attestation);
        RequireOrPublish(proofPath, proof);
        RequireOrPublish(Path.Combine(files.Root, "legacy-terminal-intent-v2.bin"), terminal);
        RequireOrPublish(Path.Combine(files.Root, "legacy-terminal-v2.bin"), terminal);
        native.ConfirmResolved(original.Epoch);
    }
}
