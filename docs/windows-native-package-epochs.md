# Windows native package and installation commitments

This layer authenticates a native package and publishes its installation epoch.
It does not prove which executable a process loaded, issue native ownership,
certify worker drain, activate the native backend, or replace the C# product.
It changes neither Contracts nor IPC, which is version 80. Windows is the only target in scope.

## Trust and artifact identity

The supported domain is the existing LocalDev deployment signature domain,
OpenBlade-Local-Dev-Deployment-Manifest-v1 followed by NUL. The product producer
acquires the existing fixed protected broker enrollment and accepted transaction.
It accepts no caller public key as installed trust. Missing enrollment, unknown
keys, disabled policy, invalid signatures and other channels deny. Stable signer
provisioning and a separately reviewed product policy remain owner dependencies.
No signing credentials, certificate-store changes or enrollment are created here.
Current machine enrollment was not inspected or provisioned in this offline stage.
The arbitrary-key algorithm fixture constructor is absent from default builds.

The package is the same write-excluding held Burn stream used for full signed
size/digest verification, role ownership reconciliation, bounded cabinet
extraction and native inventory decoding. Both role executables are extracted
from those bytes. A fixed .obid PE section emitted by build.rs contains a build
nonce plus role, target, exact source revision, sorted features, compiler version
and profile, plus v2 capabilities. The section holds at most 4096 bytes; build.rs
and both readers share that bound, and the sorted feature list is most of it.
The publisher records its full file digest and rejects a conflicting
mapping for an artifact ID across retained epochs, including abandoned complete
preparations. Rebuilding with a reused nonce and different bytes is denied.

A bound build supplies OPENBLADE_NATIVE_BUILD_NONCE (fresh lowercase 32-byte hex)
and OPENBLADE_NATIVE_SOURCE (exact 40-character revision). The marker is retained
in each executable. Ordinary builds need neither variable and emit an explicitly
unbound marker that the package producer rejects. Build metadata is identification,
not trust; only the authenticated package and protected publisher establish its
accepted mapping. The publishing caller is responsible for honest exact-source
build inputs, like the existing release process.

The version 1 app-local recipe contains exactly the Service and Session Agent
executables. Both must be native x64 PE images without CLR or delay-import
contracts. C# and Rust inspect the compiled section and canonical import names.
The fresh app directory has exactly those two files. Both link the C runtime
statically, so their only external imports are reviewed inbox Windows system DLLs,
an external platform boundary; this recipe grants no dynamic-load origin or optional
Razer/NVIDIA/THX module authority. It installs no CIG policy. Additional app-local
assets or optional module consumers require the next integrated loader/origin
recipe and their existing admission, rather than inheriting another backend.

## Records and publication

Canonical version 1 records are bounded ASCII lines with exact headers, field
counts and a final LF. NativeArtifactRecord binds identity/provenance/imports to
size and SHA-256. NativePackage binds LocalDev installation, original installer,
manifest/signature/key, original role resource, both artifacts and closure recipe.
It carries each artifact record as base64 of at most 10924 characters, and the
whole package stays within the 16384-byte record bound with both identities at
4096 bytes. NativeEpoch binds phase, random epoch, predecessor selection digest, increasing
ordinal, installation, package commitment, fresh request, both artifact IDs,
readiness and live-owner nonce. Cross-language known-answer data is checked in at
tools/openblade-windows-host/tests/data/native-package-v1.json. Its public test
scalar is deliberately known and is never enrolled.

The fixed protected namespace is ProgramData/OpenBlade/NativeEpochs. The installer
may provision that root only below the existing protected OpenBlade parent. Each
preparation reserves and keeps a fresh generation name, then creates its own app
directory. Rollback always receives a new epoch/path, even for identical package
bytes. Package evidence is immutable and separate from mutable current.bin.

Each generation retains release.json, release.sig, package.exe (the original
signed package), roles.bin (the extracted inventory), package.bin, origin.bin,
immutable phase records and both images. A process-independent reader re-verifies
the enrollment signature domain, original full package, inventory/build/role
records and retained image digests. The Rust reader trusts protected projection
custody by the authorized privileged installer; it does not re-expand Burn. This
matches the stated utility TCB: malicious Administrator/SYSTEM code or trust-state
replacement is outside the claim. The persisted signed original is independently
verifiable after Setup exits; an HMAC or live digest assertion is insufficient.

An exclusive writer file serializes publishers, without claiming native ownership.
Small records use flush, held-object atomic rename and readback. Package mapping
records are atomic too, so a torn preparation cannot poison the artifact ledger.
Large files are prepared before any phase/selection authority. Retained handles
exclude writes/deletion and retain non-reparse NTFS namespaces. Readers recheck
security, closure names, current selection and the append-only publication frontier.
Evidence loss is sticky. Native ownership retains the package before constructing
its worker; failed drain quarantines both package guards and the existing owner
fence. The guard is not dropped at admission.

| Durable point | Meaning after owner loss or reboot |
| --- | --- |
| Reservation/preparation only | No new selection; incomplete temporary files grant nothing. |
| Provisional phase before frontier | No startup has been authorized by publication. |
| New frontier before current replacement | Old/current mismatch denies; explicit recovery required. |
| Provisional current | Only the live Setup transaction may use provisional readiness; disk reconstruction denies normal authority. |
| Committed phase before frontier/current | Still provisional or interrupted, so normal reader denies. |
| Matching committed phase/frontier/current | Package commitment can be reconstructed without Setup. Loaded-origin/ownership gates still deny until supplied. |
| Unresolved | Explicit recovery only; no automatic launch, replay or fallback. |
| Retired | Package selection revoked; this is not a native drain certificate. |

Explicit recovery compares the expected frontier and immutable origin. It may
finish publication of the exact already-issued unresolved bytes after its own
crash; it performs no native work. Selecting an older otherwise valid current
record conflicts with the append-only frontier and denies. Reader history is
bounded; history maintenance and privileged backup restoration must preserve the
protocol. Injected failure tests establish software decisions, not physical
power-loss durability or filesystem/kernel compatibility.

## Setup and next boundary

SetupNativePackageTransaction wraps the existing concrete payload transaction.
It publishes the actual authenticated native package and provisional selection
before activation/startup. SetupCommandRunner uses its bound candidate health
path before final commit. Readiness must match the selected immutable Service
path/digest and the original retained launch; the existing typed status exchange
brackets actual pipe-peer and live-launch checks. Commit rechecks that launch and
binds readiness to this provisional commitment. Missing incoming origin/launch
binding denies. After attempted native activation, errors preserve unresolved
state and bypass the managed runner's automatic restoration/fallback sequence.
The default managed path keeps its previous behavior.

No production constructor selects this wrapper yet. The next integrated stage
must connect immutable generation layout to SCM/Agent launch, eagerly acquire
loaded origin before dispatch, and supply the real status/ownership provider.
The native reader is already called by installed Service admission and can return
package commitment for a correctly provisioned committed epoch. FirmwareSource
still receives no independent loaded authority and no confirmed-performance
projection. Agent entry bindings remain closed. A package record cannot fill any
of those gaps. Boot, sign-in, original-process identity, real pipe peers, native
retirement and supported feature composition require their separate verification.

Tests in this stage run only audited pure/data/crypto and private-filesystem
cases. Native binaries and the NativeAOT scratch receiver are compile/link or
read-only data inputs. No product/fixture child, IPC, SCM, installation, hardware,
network, elevation or sleep/resume validation is implied.
