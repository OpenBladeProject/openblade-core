# Shared Rust core boundaries

The Windows C# host uses the Rust report codec and selected profile merge policy
through a required packaged native component. WinUI 3, the service, Session Agent
and hardware ownership remain in C#. See the [active contract](#active-codec-and-selected-profile-merge-stage-7)
and [workspace guide](../rust/README.md) for current behavior and build requirements.

Sections describing stages 1–6 preserve the migration history. Their dormant,
developer-only and deferred-loading limits describe those original stages;
they are superseded by stage 7 for the active product.

## Ownership and dependencies

The `rust/` Cargo workspace has four initial crates:

| Crate | Owns | Depends on |
| --- | --- | --- |
| `openblade-types` | Portable value types, explicit numeric meanings, capability and evidence metadata, requested versus confirmed state, structured errors | No other OpenBlade crate |
| `openblade-protocol` | Pure report encoding, checksum and response-envelope validation, bounded typed command definitions backed by reviewed exact-model evidence | `openblade-types` |
| `openblade-domain` | Pure profile merging and decision rules; later, explicit ownership/recovery state transitions driven by host observations | `openblade-types`; protocol only if a later reviewed planner needs typed command descriptions |
| `openblade-ffi` | Versioned C exports, input validation, representation conversion, buffer handling and panic containment | The three crates above |

Dependencies point inward. Types never depend on protocol, domain, FFI, or an OS
adapter. Protocol and domain never call FFI or platform APIs. Use ordinary Rust
types internally; `repr(C)` boundary structures belong to FFI. The future runtime
and platform adapters are separate work, rather than hidden side effects in these
four crates.

The initial extraction seams are:

| Current C# owner | Portable candidate | Responsibility retained by the host |
| --- | --- | --- |
| `RazerReport.Create`, `Encode`, `ValidateResponse` in `src/OpenBlade.Protocol/RazerReport.cs` | Byte construction and validation | Selecting an admitted typed command and interpreting its payload |
| `ApplicationProfileResolution.Apply` / `ApplyDisplay` in `src/OpenBlade.Service/ApplicationProfileResolution.cs` | Merging already decoded values | Validating whole profiles and selecting the active application/power source |
| `ProfileAutomationState` in `src/OpenBlade.Service/ProfileAutomationState.cs` | Performance priority and lighting source-token selection | State lifetime, compatibility filtering, observations, recovered overrides and orchestration |
| `IRazerCommandSession` / `WindowsRazerHidSession` | Later adapter seam | Native handles, serialization, transaction allocation, cancellation, submission outcomes and retirement |
| `IpcMessageCodec` in `src/OpenBlade.Contracts/IpcMessageCodec.cs` | No initial extraction | Existing authenticated service IPC, compatibility projection and schema readers |

The Windows C# service remains the sole owner of the laptop control HID handle
and the authoritative hardware writer throughout phase 1. WinUI 3 and the
Session Agent keep their current roles. Any call to a pure helper supplies
values and receives a result; it does not transfer ownership, admission, or
permission to submit a command. Fresh exact-device, capability, controller,
power, session, ownership and readback checks remain at the existing write path.
An invalid response must retain the current ambiguity and recovery treatment.

Future macOS uses a native SwiftUI/AppKit frontend; the planning assumption is
that macOS already works. Linux starts with a CLI; GTK4/libadwaita is the proposed
GNOME-oriented frontend, pending a toolkit decision. Frontends consume typed,
authenticated commands/status from their platform host. OS adapters own device
discovery, identity, HID I/O, privileges, process identity, display/audio/input,
power events, persistence and atomic journal writes. Windows executable matching
is a Windows contract, not a portable case-folding rule.

The current catalog contains 19 model profiles. Preserve exact-model command
evidence and per-feature limits; official source-reviewed support and physical
validation are separate metadata. A new OS backend needs reviewed admission and
software failure/recovery verification. Record its physical-validation status
separately; it cannot inherit Windows physical results. Physical validation is
not a blanket prerequisite for official source-reviewed support. Porting a codec
or consuming a synthetic fixture establishes neither model support nor a
hardware result.

## Codec C ABI v1

The frozen codec declarations are in the [C header](../rust/crates/openblade-ffi/include/openblade.h).
Rust, C and C# tests verify their natural record sizes and offsets on each native
CI target. The stateless synchronous exports encode reports and validate responses.
They perform no I/O and retain no handles, callbacks, tasks, policy state or caller
memory. The additive stage 6 policy extension is specified below.

Use C linkage and the C calling convention (`extern "C"` / C# `Cdecl`). Export
`ob_abi_version()` returning `uint32_t` value `0x00010000` for ABI 1.0. Every work
export has a versioned name (`ob_v1_encode_report`,
`ob_v1_validate_response`) and an initial
`uint32_t abi_version` argument. Require an exact supported version before
reading operation inputs. A missing export, wrong architecture, incompatible
version or layout fails the helper admission with a visible diagnostic before
hardware work. Never guess a layout or silently switch implementations in a
live hardware operation. Stage 4 deferred startup selection and
recovery behavior explicitly.

Use these representation rules:

- Integers are `uint8_t`, `uint16_t`, `uint32_t` or `int32_t`; lengths and
  capacities are `uint32_t` byte counts. Never expose Rust enums, `bool`,
  `usize`, managed objects, platform `long`, strings or collections directly.
- Boolean values are `uint8_t` 0/1. Optional fields have a separate `uint8_t`
  presence flag, also 0/1. Reject other flags and unknown wire enum values at the ABI
  adapter; absence remains distinct from `false`, zero and an empty string.
- Strings use a borrowed `const uint8_t*` plus explicit length, strict UTF-8,
  without a terminator. Binary slices use the same pointer/length convention.
  A null pointer is valid only for a zero-length slice. No input/output overlap;
  immutable inputs may share storage; writable ranges must be disjoint from all
  other ranges. The caller guarantees accessible pointers for each declared range.
  Rust cannot validate arbitrary pointer provenance.
- Boundary records use natural C alignment, fixed declaration order and a
  leading `uint32_t struct_size`. Each version freezes field offsets, size,
  alignment and reserved-zero fields in the header. C# uses matching sequential
  layout and verifies sizes/offsets; do not force packed layout. Serialize no
  in-memory struct directly to IPC or disk. Any future serialized binary format
  must specify little-endian fields separately from native ABI memory layout.
- Report inputs are transaction/class/ID bytes and a payload slice; response
  validation adds a response slice and optional expected payload length.
  Encoding returns exactly 90 protocol bytes, excluding Windows' report-ID
  prefix. Validation returns a copied payload of at most 80 bytes.
- The profile helper takes only the bounded scalar/key projection documented below.
  Curves and all other profile values remain host-owned.

Outputs are caller-owned. A variable result is returned as an output byte buffer,
its capacity, and `uint32_t* written_or_required`; profile string results use
caller-owned buffers with the same rule. A zero-capacity/null buffer is a size
query. If capacity is insufficient, return `buffer-too-small`, report the exact
required byte count, and change no result bytes. Success writes only the result
range and reports its length. Other failures write no result bytes and set the
length to zero. Input and output borrows end when the call returns; Rust never
allocates a pointer that C# must free, and never retains a pinned managed buffer.
The .NET wrapper returns owned copies after calls complete.

Every work export returns a `uint32_t` status and fills an optional caller-owned
error record with `uint32_t` fields `struct_size`, `category`, `detail`,
`required_length` (16 bytes, alignment 4). Require exactly size 16 if supplied;
an invalid error-record size returns invalid-argument without writing that
record. The return status equals `category`; success clears detail/required.

| Category | Numeric meaning | Detail |
| --- | --- | --- |
| success | 0 | 0 |
| abi-mismatch | 1 | 0; host reports requested and observed ABI separately |
| invalid-argument | 2 | 1 payload too large; 2 invalid length; 3 invalid flag/enum; 4 invalid UTF-8; 5 required value/pointer absent; 6 invalid record size |
| buffer-too-small | 3 | 0; required byte count is in `required_length` |
| protocol-validation | 4 | Existing `HidProtocolValidationFailure` numeric meaning below |
| internal-failure | 5 | 0; no exception text or native pointer escapes |

Protocol details retain `ReportId=0`, `EnvelopeLength=1`, `Checksum=2`,
`TransactionMismatch=3`, `CommandClassMismatch=4`, `CommandIdMismatch=5`,
`Status=6`, `PayloadLength=7`, `PayloadValidator=8`. The pure report validator
emits only 1 through 7; report ID and semantic payload checks remain outside it.
The production host adapter maps category/detail to the existing taxonomy,
preserving
validation failure and submission state. A pure-helper failure never means a
hardware command was submitted, and cannot reinterpret an earlier ambiguous
submission as safe to retry. Fixture `payload-too-large` maps to invalid-argument
detail 1; `destination-length` maps to detail 2 for the current C# exact-span
API; `baseline-null` maps to detail 5. The codec ABI's capacity-based output
buffer rule is separately tested and does not change C#'s exact-span contract.

Contain panics inside every work export with an unwind-catching boundary;
validate and compute into bounded temporary storage before publishing output.
No panic or C++/managed exception may unwind across the ABI. Both checked-in build
profiles use unwind panic handling. The boundary deliberately forgets a caught
panic payload so its destructor cannot trigger another unwind across C; this
exceptional path leaks that payload. The process panic hook remains unchanged
and may emit diagnostics. Aborts, allocation failure and process faults cannot
be converted into recoverable errors.
Independent calls may run concurrently using disjoint caller buffers. No helper
acquires the service hardware queue or changes host serialization. The first
binding must test parallel calls, malformed lengths/flags/UTF-8, buffer queries,
ABI mismatch, error mapping and panic containment before adoption.

ABI versions, parity-fixture schemas, product SemVer, service IPC, Session Agent
IPC, exported profiles and durable journals are independent contracts. Keep
ordinary IPC/profile/journal schemas, numeric meanings and migrations unchanged.
Do not pass existing serialized profiles through FFI as an implicit new ABI.

### Frozen records and loading

| Record | Size / alignment | Fields in offset order |
| --- | --- | --- |
| `ObReportRequestV1` | 8 / 4 | `struct_size` u32 at 0; transaction/class/ID/reserved bytes at 4/5/6/7 |
| `ObValidationOptionsV1` | 12 / 4 | `struct_size` u32 at 0; signed expected length i32 at 4; presence byte at 8; three reserved-zero bytes at 9 |
| `ObErrorV1` | 16 / 4 | `struct_size`, category, detail, required length u32 at 0/4/8/12 |

The request and validation-options records are required. An absent expected length
ignores the stored signed value; presence must be exactly 0 or 1. All reserved
bytes must be zero. Pointer/length pairs remain separate parameters, so no native
pointer width is embedded in a record. Lengths/capacities are u32 byte counts.
Output pointers are never returned. Error-record size is checked before ABI
version, and ABI version before operation records. A valid supplied length slot
is set to zero on all failures except insufficient capacity. Invalid error-record
size leaves that error record untouched. See the header for full precedence and
pointer preconditions; no ABI can validate arbitrary pointer provenance.

`OpenBlade.Core.Native` targets plain `net10.0` and has no Protocol or Windows
assembly dependency. It loads only a normalized absolute path with the platform's
exact library filename, checks process architecture, all managed layouts, exports
and exact ABI version, and returns typed boundary/loader errors. Explicit Cdecl
function pointers allow independent module instances; a SafeHandle reference
covers every native call so concurrent disposal cannot unload executing code.
It validates native status/error consistency and result bounds before exposing
owned bytes. This is a developer trust boundary, not artifact authenticity or
production activation policy. The packaging contract below covers artifact
provenance; stage 7 specifies the required production loading policy.

`OpenBlade.Native.slnx` and `rust/Test-NativeInterop.ps1` are explicit developer
paths. The product graph also consumes `OpenBlade.Core.Native` through Protocol
and builds Rust. Developer interop tests require `OPENBLADE_NATIVE_LIBRARY` to
name the actual built artifact;
a missing artifact fails. Rust unit tests inject a panic privately in the same
exported work boundary; production libraries expose no panic/test entrypoint.
C header smoke tests link and call the real shared library. ABI capacity tests
are distinct from the canonical C# exact-destination-span error contract: a
91-byte ABI output capacity succeeds and writes only 90 bytes.

## Shared parity baseline

[The fixture contract](../tests/Fixtures/Parity/README.md) defines the exact JSON
consumed by C# and the Rust report/profile tests. Stage 6 consumes all 14 profile cases.
It pins the reviewed source revision and independent expected values. These are
synthetic software vectors, separate from production capture/admission evidence.
Existing tests remain in place.

Migrated helpers run both readers against these same files. Full profile
validation, application selection, override lifetimes, hardware admission,
transport outcomes, ownership, durable recovery and platform behavior retain
their existing tests plus the focused parity tests for the slice migrated.

## Dormant Windows packaging (stage 5)

`build/Publish.ps1` builds the first-party `openblade_ffi.dll` for
`x86_64-pc-windows-msvc` from this checkout's committed source with Rust 1.99.1,
the locked Cargo graph and the selected x64 MSVC/Windows SDK tools. At the
original stage 5 boundary, ordinary .NET builds remained independent of Rust;
stage 7 makes the native component part of ordinary builds and tests.
Release packaging needs rustup, the pinned toolchain, `cargo fetch --locked`
and MSVC Build Tools with the Windows SDK; hosted installer CI prepares the pin.
The DLL statically links the CRT and admits only its reviewed Windows system
imports. It adds no installed runtime prerequisite. Both Rust profiles retain
unwind containment. No vendor code or new firmware capability is included.

The payload contains the DLL, `OpenBlade.Native.provenance.json` and
`OpenBlade.Native.NOTICE.txt`. PE file versions follow the existing stable,
Nightly and LocalDev build identity. Provenance records the full source revision,
canonical Git blob identities for source inputs, exact compiler output,
MSVC/SDK versions, linker/resource-compiler hashes,
target, flags, imports, ABI, notice hash and both unsigned and final DLL hashes.
Source identities use Git's canonical blobs so LF/CRLF checkout conversion does
not change them; fresh dirty/untracked-input checks still guard compilation.
Path remapping and `/Brepro` remove checkout-dependent debug paths and PE link
timestamps. Reproducing an unsigned DLL also requires the recorded MSVC/SDK and
Rust toolchain; Authenticode timestamped bytes are intentionally separate.
The notice includes OpenBlade's GPL text and the pinned standard-library notices.

Packaging refuses dirty or untracked native inputs and a mismatching supplied
source revision. It builds into this checkout's `artifacts/` staging directory,
never a sibling worktree's output. It inspects PE architecture/imports without
loading code, verifies version/source/hash/notice identity, and rejects unexpected
CRT or delay-loaded dependencies. The observed imports are `kernel32.dll`,
`ntdll.dll` and `api-ms-win-core-synch-l1-2-0.dll`; the last is a
[Windows API-set contract present since Windows 10](https://learn.microsoft.com/en-us/uwp/win32-and-com/win32-apis),
within the existing Windows 11 minimum. [Static CRT linkage](https://doc.rust-lang.org/reference/linkage.html#static-and-dynamic-c-runtimes)
is selected explicitly. Release builds reject external Cargo configuration,
clear compiler wrappers/profile overrides and select the recorded linker.
Existing optional Authenticode signing covers
the native DLL before Burn embeds it; provenance is then updated to its final
hash and signature trust is checked. The original unsigned hash is retained
independently across signing; unsigned artifacts must match both hash fields.
Unsigned development/release preflights
retain the existing visibly incomplete signing status. No new trust root exists.

Setup's existing complete-file hash manifest, staged readback, atomic replacement
and rollback carry these three dormant files with the rest of the payload.
The real Burn layout and upgrade-identity probe run unchanged. Stacked PRs run
the same .NET build/contracts/installer gates as PRs to `main`.

Service and Capture directly consume Protocol; FirmwareSmoke references Protocol
and performs hardware work through typed service IPC. Adding a native dependency
to Protocol would affect all three publish graphs. Stage 5 adds none: installed
Service, Session Agent, Tray and developer Capture/FirmwareSmoke do not reference
the native wrapper or load the DLL. A later runtime handoff must explicitly
coordinate these consumers and deployment failure handling. The service retains
all device ownership, per-write checks, submission outcomes and durable recovery.

`rust/Compare-Shadow.ps1 -LibraryPath <absolute-openblade_ffi.dll>` runs a bounded
Windows x64 developer test path. It enables only the conditional
`OpenBladeNativeShadow=true` test reference/source and compares the actual C#
`RazerReport` results with Rust over all 29 canonical report vectors. It compares
bytes, copied payloads and numeric error taxonomy without opening a transport or
making an extra hardware call. C# remains authoritative. Exact-span destination
errors are compared through an explicit 90-byte projection because ABI capacity
and C# destination-length contracts differ. Default solution builds exclude the
shadow source/reference; no product flag, IPC field or saved schema is added.

## Migration stage reviews

Each migration stage requires a real Claude advisory review of its exact scope.
Record the reviewed base and head commits. Codex validates the findings against
that revision, fixes supported issues, and verifies the affected checks. Record
the findings disposition and resulting head in the stage's PR. Attribute the
review to the revision Claude actually inspected; a later fix commit does not
inherit that review. This requirement applies to the Rust migration stages.

## Dormant profile projection (stage 6)

`openblade-domain::apply` and `apply_display` implement only the decoded merge
slice. They remain allocation-free `no_std` with borrowed UTF-8 keys; no alloc or
production registry dependency is added. The domain fixture reader uses the same
pinned test-only serde dependencies as protocol. Both readers consume the single
14-case corpus without regenerating expectations.

The additive exports `ob_policy_abi_version`, `ob_v1_apply_profile` and
`ob_v1_apply_display` negotiate policy v1 (`0x00010000`) independently. Existing
codec exports, records, exact version checks and callers stay compatible. Codec
loading does not require policy exports. The C header freezes all policy fields:

| Record | Size / alignment | Offsets |
| --- | --- | --- |
| `ObOptionalI32V1` | 8 / 4 | value 0; presence 4; reserved bytes 5 |
| `ObOptionalUtf8V1` | 12 / 4 | offset 0; byte length 4; presence 8; reserved bytes 9 |
| `ObDisplayProjectionV1` | 44 / 4 | size 0; refresh key 4; HDR 16; color key 24; brightness 36 |
| `ObProfileProjectionV1` | 88 / 4 | size 0; performance 4; fans 12; fan mode 20; RPM 24; fan presence 28; display presence 29; reserved 30; gaming 32; display 40; fan source 84 |

Each call borrows one input key buffer; offsets/lengths select strict UTF-8
ranges. Empty keys have presence 1 and length 0, distinct from absent keys.
Output keys concatenate refresh then color into a caller buffer, with offsets
relative to that output. No terminators, pointers or collections are returned.
Flags are exactly 0/1 and reserved bytes zero. Absent scalar/key payloads and
absent nested objects are ignored. Required input/result sizes are exact; result
records need only a readable size prefix until admitted. There is no pointer
provenance validation. All writable ranges are disjoint from each other and
inputs, including result, error and length slots.

Admission order is error-record size, ABI version, written pointer, result size,
baseline record, input key buffer, baseline fields, optional override, then
output capacity. Errors/query leave result records and key bytes untouched;
written is required length for insufficient capacity and zero for other errors.
A zero-length result succeeds with null/zero key capacity. Publication happens
only after all fallible checks and private panic injection. The existing error
record/categories and panic containment apply. Malformed UTF-8 maps to invalid
argument detail 4; out-of-range/overflow key slices to detail 2; invalid flags or
legacy synthesized fan mode to detail 3. Baseline absence maps to detail 5.

Policy integers are scalar enum payloads, not a new validation boundary. Unknown
performance and inherited fan values, contradictory fan fields, empty/control/
long keys and brightness 101..255 survive as they do in C#. Only legacy fan
synthesis rejects fan modes outside 0..3, matching `FromLegacy`'s constructor.
Direct native callers may supply other signed brightness/fan-control scalars;
they grant no host admission. The managed adapter accepts only values representable
by the existing typed host records. The next activation stage must preserve host
validation and exception order: map missing baseline to `ArgumentNullException`
for `baseline`, and failed legacy synthesis to `ArgumentOutOfRangeException` for
`mode`. Do not turn wire parsing into whole-profile validation.

`NativeProfilePolicy` is a separate developer-only loader with a platform-light
Contracts reference. It reconstructs from `baseline with`, retaining all fields
outside this slice. Fan source 0/1/2/3 selects none, the exact baseline object,
the exact override object, or the existing host legacy defaults. Arbitrary CPU/
GPU curves and their higher-temperature policy therefore stay host-owned.
CustomPerformance, GpuClockTuning, Lighting and GamingModeKeyPolicy keep their
existing host-side override branches. Null application overrides bypass projection
without consuming keys and return the
same baseline object, including legacy UTF-16 strings; baseline absence is checked
before encoding. Consumed keys require strict UTF-8, so present overrides with
unpaired UTF-16 surrogates return an encoding error. Activation must account for
this wire limit without silently changing host validation. A present override
retains the existing unconfigured display and behavior objects. The direct display API requires a nonnull baseline;
the unused C# null-baseline short-circuit quirks are outside this ABI contract.

Real C# tests link the unchanged C# merge helper as their oracle, consume all 14
canonical cases and verify complete profile equality plus preservation sentinels.
C/Rust/C# layout, UTF-8, optional, buffer, error and lifetime checks execute on
Windows x64, Linux x64, Intel macOS and ARM macOS through native CI. These are
software checks. Product solution/project references, callers, IPC, saved
schemas, service ownership, hardware admission and product version remain
unchanged at the original stage 6 boundary (product version 0.56.1). Runtime
activation followed in stage 7; stage 6 itself added no production fallback
or native selection.

The policy loader checks only policy exports/version and its own path, platform
and layout admission. A separate test-only C library exports policy v1 without
any codec symbols to verify independent loading; its work stubs fail and never
supply parity results. `Test-NativeInterop.ps1` builds this fixture and supplies
`OPENBLADE_POLICY_ONLY_LIBRARY` to the developer tests. All merge and sentinel
results still run against the actual Rust library. Native workflow path filters
include Contracts and the linked authoritative C# merge helper. Contracts codec
checkout/analyzer LF policies agree so this newly cross-platform build compiles
unchanged canonical source on Windows, Linux and macOS.
## Active codec and selected profile merge (stage 7)

The C# Windows host now calls the proven codec and profile merge through the
mandatory packaged native component. The codec and policy ABI versions remain
v1; both IPC protocols and persisted schemas are unchanged. Activation is a
behavior-preserving internal refactor; packaging follows normal [product versioning](../VERSIONING.md).
The original stage 7 implementation was stacked on exact predecessor
`69f66ba673c0523c0521b09b286a7d09f8137488`; integration refreshes that reviewed
scope from current main and recalculates the packaging version there.

`RazerReport` preserves its copied request payload, exact 90-byte destinations,
payload-before-destination argument errors, response validation order, protocol
numeric categories and messages. The native capacity contract is projected to
that exact host contract. A bounded host projection preserves the static encoder's
legacy clear/header-before-copy behavior when payload and destination overlap;
the ABI input/output buffers remain disjoint. The report-ID prefix, transaction allocation,
command-specific payload callbacks and batch SET sequencing remain outside the
codec. Native boundary failures retire the serialized session and drain queued
work; a failure after possible submission never selects a fallback or replays.

The profile adapter sends one-byte source tokens instead of user key text.
Selection returns the exact original host string, preserving empty, control,
long and unpaired-surrogate UTF-16 keys and equal/different source occurrences.
No key equality semantics are introduced. Fan curves and non-projected fields
remain host-owned; null override identity, display null branches and legacy fan
constructor exceptions retain their managed behavior.

Startup uses a fixed application-base path and a manifest embedded in the managed
native assembly. Admission checks product identity, architecture, ABI and the
exact native hash before loading. The process retains the file lease and loaded
modules through shutdown cleanup. Missing, incompatible and altered components
produce bounded redacted failures. Unrecoverable background policy errors receive
a fixed critical diagnostic and nonzero process status before bounded shutdown
cleanup; observation/lifecycle loops do not retry those errors. Release signing
precedes the managed hash
binding; ordinary developer builds create their own manifest from current inputs.

Protocol is shared by Service, Capture and FirmwareSmoke; the native reference
and payload follow those graphs. Startup gates precede laptop-control work. The existing
fresh identity, capability, controller, power, session, ownership and readback
checks, journals and handback remain host code. The capture-backed lighting
semantic manifest still checks its command/geometry/layout/known-answer contract;
source edits alone do not change that contract. No firmware command or model
capability is added, and no installation or physical validation is claimed.

Test-only frozen managed baselines preserve an independent oracle after the
switch. The original canonical fixtures are retained. Native interop runs on all
four existing host targets; affected Windows integration and package tests check
the coordinated payload and failure paths. See [native development](../rust/README.md)
for the new ordinary build requirements.

## Developer-only read-only status CLI

The separate std binary [openblade-status-cli](../rust/crates/openblade-status-cli/README.md)
reads the existing Windows service's cached GetStatus snapshot through its v51
read-only compatibility path (current service IPC is v78). It has no dependency
on the four shared libraries and is excluded from product packaging. JSON and
OS/runtime dependencies belong to this CLI alone; the existing inner no_std,
unsafe and inward-dependency rules remain authoritative.

The guide records the compatibility lease's temporary thermal-admission effect,
redaction, unknown telemetry freshness, identity limits and bounded cancellation.
The Windows pipe module is the transport seam for future OS work. Other systems
currently return unsupported transport without reporting invented device state.


## Automation selection (stage 8)

The bounded extraction selects a host-owned value and its provenance in
`ProfileAutomationState.ResolvePerformance` and `ResolveLighting`. Performance
selects persistent override, temporary override, then profile. Lighting uses
the same priority on known power sources, then profile presence, then confirmed
firmware fallback. On `ExternalUnknown`, observed lighting wins with
`ConfirmedFirmwareState` provenance; absent observations use the fallback profile
with `PowerProfile` provenance, ignoring overrides and application provenance.

Rust receives presence flags and returns origin/provenance tokens. Settings,
enum payloads, strings, curves and object references stay in C#. Mapping retains
the exact original values, including zero, unsupported performance scalars and
null lighting inside a present override. Known-source confirmed fallback retains
`confirmed.Lighting ?? profile.Lighting`; unknown-source observation retains
lazy access to saved profile lighting.

The additive `ob_automation_abi_version` and `ob_v1_select_automation` exports
negotiate automation ABI `0x00010000` independently of codec/profile ABI v1.
The C header freezes the natural 16-byte request and 12-byte result, alignment 4,
operations, flags and token numbers. The fixed result needs no variable buffer
or capacity query; required `written` is always zero. Admission checks error size,
version, written pointer, record pointers, result/request sizes, flags and
operation in order. Failure leaves the result unchanged, including caught
panics before publication. Calls retain no memory.

`NativeAutomationPolicy` checks its own exports/version/layouts, uses Cdecl
and SafeHandle lifetime references, and validates returned tokens/errors.
Production admits it from the same fixed, hash-bound artifact before hardware
composition, retains it through shutdown and disposes it on failed admission.
Errors propagate through existing `NativeComponentException` supervision.
A separate C loader fixture proves independence from codec/profile exports;
it never supplies parity results or enters the product payload.

Managed result admission rejects tokens that cannot address this request's host
values, including lighting overrides on unknown external power. Priority remains
Rust policy and is checked against the independent oracle. Performance ignores
lighting-only flags as specified by the ABI; host reconstruction also preserves
legacy/undefined managed profile-source values without expanding the wire schema.


The managed oracle freezes the actual helper/resolver bodies at
`9e53ed92bbe3985e1ea899ed6c1124f6f6dd020e`, adapting only instance fields to
explicit parameters. Eighteen shared literal cases cover priority, absence and
provenance. All presence combinations compare the native binding and actual host
helpers with that oracle, including distinct equal object references, present
overrides containing null, zero and unsupported performance values. Whole-state
tests retain unprojected custom/fan objects and source reversion. Rust/C/C# tests
cover layout, malformed inputs, publication, panic containment, independent
negotiation and concurrent disposal. An isolated Windows failure probe checks
host validation order and no published desired state after lifetime failure.

Compatibility predicates and recovered HyperBoost priority remain C# because
they select active candidates before these helpers. C# retains mutable state,
override lifetimes, foreground/path matching, power transitions, validation
order, observed fallback construction, saved intent and suppression. Read-only
recovery may publish a power source before destination automation is consumed:
maintenance still requires source agreement and never replays uncertain commands.
Fresh device/controller/power/session/ownership/readback admission, queues,
cancellation, journals and hardware writes are unchanged.

This behavior-preserving extraction has version impact `none`. IPC versions,
persisted schemas, model capabilities and vendor definitions are unchanged.
The next small seam is source compatibility for decoded performance candidates,
with separate parity for legacy values; temporal state and fan backend policy
remain host-owned.
