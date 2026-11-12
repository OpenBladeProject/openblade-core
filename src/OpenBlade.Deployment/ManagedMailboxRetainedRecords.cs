namespace OpenBlade.Deployment;

internal static partial class ManagedMailboxRecovery
{
    // One bounded, read-only transfer view. Discard it before the next transfer;
    // it never holds all retained runs or grants authentication.
    private sealed class RetainedRecords
    {
        internal byte[] BindingBytes { get; }
        internal ManagedMailboxBuildBinding Binding { get; }
        internal byte[]? Projection { get; }
        internal byte[]? TransferHistory { get; }
        internal byte[]? EpochIntent { get; }
        internal NativeEpochRecord? Epoch { get; }
        internal byte[]? MappingBytes { get; }
        internal byte[]? Proof { get; }
        internal byte[]? Intent { get; }
        internal byte[]? Completed { get; }
        private readonly byte[]? receipt;
        private readonly byte[]? originalExit;

        internal RetainedRecords(WindowsNativeEpochFiles files, ManagedMailboxSelection selected)
        {
            byte[]? Optional(string suffix) => ReadOptional(files, PathFor(files, selected, suffix));
            void Exact(byte[]? actual, byte[] expected)
            {
                if (actual is not null && !actual.AsSpan().SequenceEqual(expected))
                { throw new IOException("Retained transfer record differs from its immutable inputs."); }
            }
            Exact(Optional("selection"), selected.Encode()); Exact(Optional("request"), selected.Request); Exact(Optional("run"), selected.Run);
            string id = ManagedMailboxRun.Parse(selected.Run).Run;
            BindingBytes = Read(files, Path.Combine(files.Root, "run-binding-" + id + ".bin"));
            Binding = ManagedMailboxBuildBinding.Parse(BindingBytes);
            if (Binding.RunDigest != NativeCanonical.Digest(selected.Run) || Binding.Installation != selected.Value.Installation)
            { throw new IOException("Managed original signed binding differs."); }
            if (!Read(files, Path.Combine(files.Root, "run-" + id + ".bin")).AsSpan().SequenceEqual(selected.Run))
            { throw new IOException("Managed original run differs from immutable history."); }
            byte[]? history = ManagedMailboxHistory.ReadRun(files, id);
            TransferHistory = ManagedMailboxHistory.ReadTransfer(files, selected);
            if (TransferHistory is not null)
            {
                byte[] original = ManagedMailboxHistory.ParseTransfer(selected, TransferHistory);
                Exact(history, original); history = original;
            }
            if (history is not null) { Projection = ManagedMailboxHistory.ValidateRun(selected.Run, BindingBytes, history); }
            receipt = Optional("receipt"); originalExit = Optional("original-exit");
            Exact(receipt, Receipt(selected)); Exact(originalExit, OriginalExit(selected));
            if (originalExit is not null && receipt is null) { throw new IOException("Original exit lacks its quiescent receipt."); }
            EpochIntent = Optional("epoch-intent"); MappingBytes = Optional("mapping");
            if (EpochIntent is not null)
            {
                Epoch = ParseEpochIntent(selected, EpochIntent);
                RequireOriginal(); Exact(MappingBytes, Mapping(selected, Epoch));
            }
            else if (MappingBytes is not null) { throw new IOException("A mapping without its immutable binding intent remains fenced."); }
            Proof = Optional("native-proof"); Intent = Optional("terminal-intent"); Completed = Optional("terminal");
            if (Proof is not null)
            {
                RequireOriginal();
                var proof = ManagedMailboxNativeCustody.ParseResolution(selected, Epoch, Proof);
                // ResolveOne publishes both managed bindings before a reserved
                // proof. Missing predecessors contradict even a proof-only cut.
                if (proof.Epoch != "not-reserved" && (Epoch is null || MappingBytes is null))
                { throw new IOException("Reserved native proof lacks its managed binding predecessors."); }
            }
            if (Intent is not null) { ValidateCandidate(selected, Intent); }
            if (Completed is not null)
            {
                if (Intent is null || !Completed.AsSpan().SequenceEqual(Intent))
                { throw new IOException("Completed terminal intent differs from its terminal."); }
                ValidateCandidate(selected, Completed);
            }
        }
        internal void RequireOriginal()
        {
            if (receipt is null || originalExit is null)
            { throw new IOException("Exact original process and transport receipt are unconfirmed."); }
        }
        internal void RequireCompleted()
        {
            if (Completed is null) { throw new IOException("Required immutable transfer evidence is missing."); }
        }
        private void ValidateCandidate(ManagedMailboxSelection selected, byte[] candidate)
        {
            RequireOriginal();
            if (Proof is null) { throw new IOException("Required immutable transfer evidence is missing."); }
            var proof = ManagedMailboxNativeCustody.ParseResolution(selected, Epoch, Proof);
            _ = NativeCanonical.ParseLines(candidate, "OpenBlade.ManagedMailboxTerminal.v2", 7);
            if (!candidate.AsSpan().SequenceEqual(Terminal(selected, MappingBytes, proof.Disposition, Proof)))
            { throw new IOException("Terminal evidence differs from the exact original request."); }
        }
    }
    private static NativeEpochRecord ParseEpochIntent(ManagedMailboxSelection selected, byte[] intent)
    {
        var fields = NativeCanonical.ParseLines(intent, "OpenBlade.ManagedMailboxEpochIntent.v2", 3);
        var epoch = NativeEpochRecord.Parse(Convert.FromBase64String(fields[1]));
        if (!intent.AsSpan().SequenceEqual(NativeCanonical.Lines("OpenBlade.ManagedMailboxEpochIntent.v2",
            NativeCanonical.Digest(selected.Encode()), Convert.ToBase64String(epoch.Encode())))
            || epoch.Request != selected.Value.Request || epoch.Installation != selected.Value.Installation || epoch.Package != selected.Value.Package)
        { throw new IOException("Transfer epoch intent differs."); }
        return epoch;
    }
}
