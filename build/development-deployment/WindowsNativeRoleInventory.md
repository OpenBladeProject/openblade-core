# Windows native role inventory (Phase 9i)

The LocalDev broker verifies a canonical release manifest against its enrolled
key, verifies the complete staged installer against the signed size and SHA-256,
and rechecks the installer before launching it. The manifest authenticates the
outer package, version, and source revision; it carries no native role hashes.

`Publish.ps1` stages the installed tree and Setup; `installer/Bundle.wxs` adds
that tree as Burn payloads of `OpenBlade.Setup.exe`. Setup builds a SHA-256
inventory from its adjacent payload directory and checks the copied tree before
activation. That proves copy fidelity and does not independently authenticate
the inventory. The pinned product package authoring and protected startup path
have neither a native-role inventory resource nor a trusted offline reader
that can return one from the authenticated Burn bytes. The separately packaged
`RustFixtureCandidate.ps1` outputs (`openblade-windows-host.exe` and
`openblade-rust-session-agent.exe`) and their scratch signature carry no
authority for either installed candidate.

The offline seam produces one private fixed-size resource for
`openblade-windows-service-candidate.exe` and
`openblade-windows-agent-candidate.exe`, the separate Cargo targets at this
checkpoint. It checks for distinct AMD64 native PE executable images and hashes
the final bytes. The record stores a Windows x64 format marker, the 20-byte
revision, four little-endian unsigned 16-bit version components, and two
32-byte SHA-256 values in Service then Session Agent order. A controlled build supplies both outputs from the same
revision and product version, under their fixed Cargo names. The PE check
cannot infer an executable's behavior or correct a mislabeled build input.

## Phase 9l offline package reader

The reader accepts the selected standard LocalDev release from the existing
signed-manifest verifier and compares its SHA-256, version, and source revision
with the selection. It opens the staged installer once with a Windows
read-sharing handle that denies write and delete sharing. The existing installer
verifier checks the size and SHA-256 through that handle. The adapter then
reads from the same handle after it is rewound.

The fixed location for a future inventory is
`WixAttachedContainer\payload\OpenBlade.WindowsNativeRoleInventory.bin`,
owned by the `OpenBladeSetup` chain package. The reader requires one exact
match and validates the embedded Windows x64 build and distinct role digests.
The adapter must inspect the complete logical declarations and physical
container membership for duplicate or aliased entries, establish unique
ownership, and bound the resource read to its fixed size. It takes the
verified stream as input. External payloads and loose files are outside
that stream.

The offline reader returns a snapshot separate from the product admission
claim consumed by `Bind`. A signature under test-only trust can establish
fixture byte identity. Product authority still requires the existing
protected enrollment and release admission and a vetted Burn adapter.

The pinned WiX 7.0.0 CLI is present locally. `InstallerValidation.ps1` uses
its path-based `burn extract` command to expand the bootstrapper and payload
containers, then checks the expanded files and manifest against the published
build. A product adapter still needs evidence that it reads the verified
snapshot, preserves physical member multiplicity, and bounds extraction
before materializing data. The pinned product authoring has no inventory entry.
Phase 9l exercises the reader in the offline deployment test project with an
inert byte fixture and an injected adapter.

## Phase 9p offline Burn fixture

From a dedicated Windows checkout, use the cached Rust 1.99.1 toolchain,
MSVC, .NET 10 SDK, and WiX SDK 7.0.0. Supply a full local commit ID and a
new scratch directory.

```powershell
./tests/Build.Tests/WindowsNativeRoleBurnFixture/Test-Bundle.ps1 `
    -SourceRevision af61a8c842429bc0564390dee39b466ee0da9023 `
    -ScratchDirectory C:/tmp/openblade-native-role-burn-fixture
```

The test builds both AMD64 candidate targets offline from a Git archive of
the selected commit. The encoder uses that commit ID, its four-part product
file version, and the final executable bytes. WiX builds a scratch bundle
using plain-text placeholders for its bootstrapper application and primary
chain executable. The fixed `OpenBladeSetup` package owns the inventory and
both role payloads under `WixAttachedContainer\payload`.

The test holds a read-sharing guard on the bundle during WiX's path-based
extraction and checks the manifest declarations, exact inventory bytes and
both extracted role hashes. `proof.json` records the source, version, bundle
hash, and extracted payload hashes. This build validation covers the authored
fixture. A production adapter still needs same-handle access, physical member
multiplicity checks, and bounded extraction.
