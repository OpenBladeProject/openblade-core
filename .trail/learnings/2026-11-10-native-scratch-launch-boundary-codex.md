# Retain a NativeAOT scratch receiver and apply loader policy before startup

Decided: the old caller-owned FDD directory seal cannot exclude a previously
opened WRITE_DAC handle and depends on clean disposal for owner cleanup. Scratch
launch v3 instead retains one native x64 NativeAOT receiver with ordinary file
and directory identity leases. The native-v2 digest binds its name, size and
bytes; the PE/debug-header check distinguishes the build form, not authenticity.
Initial code provenance still comes from the fixture build and copied artifact.

Both the C# receiver and its separately build-hash-pinned Rust readiness child
use the same suspended CreateProcess path: explicit application/CWD, bounded
non-inherited environment, exact inherited handle list, creation-time extension
point disable, block-non-Microsoft binaries and prefer-System32 policy. Compare
the original kernel creation generation and fixed image path, query policies on
the retained original process before resume, and refuse every failure without
an alternate launch. C# and Rust independently query the live issuer policy;
verifier environment variables and sampled directory ACLs attest no launch.

Windows/Microsoft signing authority is the external module trust boundary.
Prefer-System32 is a preference, not an exact dependency identity claim. This
is not arbitrary memory-injection protection. Native compiler/link success and
data-only NTFS/parity tests do not prove real child startup, policy compatibility,
peer IPC or crash/restart behavior. Those need a separately authorized native
rehearsal. No ACL denial is installed; process loss releases ordinary handles
without removing the owner's cleanup rights. Obsolete FDD/launch-v2/sealed inputs
are refused; no unowned seal is adopted or experimentally orphaned.

Build: use cached ILCompiler 10.0.9, offline NuGet sources, win-x64 Release,
OpenBladeCandidateReceiptFixture=true and OpenBladeScratchNativeReceiver=true.
The latter references Setup/Broker as libraries without production entry points
and enables only fixture-specific diagnostic source generation. Final ILC trim
and AOT errors remain fatal; disabling whole-library source analyzers does not
suppress final reachable-code analysis. Pass the compiled Rust fixture through
OpenBladeCandidateReceiptRustProbe, then pass the native receiver to managed
tests as OpenBladeScratchNativeReceiverPath. Default production builds retain
their entry points and reflection serializer; installed receipt schema stays 2.
