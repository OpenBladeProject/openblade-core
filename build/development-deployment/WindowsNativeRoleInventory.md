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

A future product reader must authenticate the selected package using the
applicable existing product trust, independently establish its product purpose,
and read the single fixed resource from the **same authenticated snapshot**.
It must reject missing or duplicate resources and bind the authenticated
package SHA-256, product version, and source revision to the returned bytes.
For LocalDev, enrollment and signed-release admission remain the broker's job;
the installation ID identifies an enrollment, not a package. The package digest
stays outside the inventory because embedding it would be circular. A loose
resource, an arbitrary expected role digest, or an authentic scratch package
cannot substitute for the trusted reader.

Only a successful bind exposes the fixed role's expected SHA-256 for a future
Phase 9f protected-file read. Phase 9i does not implement a product Burn
extractor, modify package authoring or Setup, or activate the candidates. The
seam is compiled only by the offline deployment test project. Tests use
deterministic inert PE headers, an injected product reader, and scratch trust.
