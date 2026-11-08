# Shared Rust core boundaries

This contract was pinned in stage 1. Stages 2 and 3 added the workspace and pure
report codec. Stage 4 implements the codec C ABI and a separate developer-only
.NET binding with real cross-language tests. Production C# behavior is unchanged;
product loading, packaging and runtime handoff remain later stages. See
[the workspace guide](../rust/README.md) for developer commands.

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
| `ProfileAutomationState` in `src/OpenBlade.Service/ProfileAutomationState.cs` | Later explicit decision rules | State lifetime, observations, manual override priority and orchestration until separately migrated |
| `IRazerCommandSession` / `WindowsRazerHidSession` | Later adapter seam | Native handles, serialization, transaction allocation, cancellation, submission outcomes and retirement |
| `IpcMessageCodec` in `src/OpenBlade.Contracts/IpcMessageCodec.cs` | No initial extraction | Existing authenticated service IPC, compatibility projection and schema readers |

The Windows C# service remains the sole owner of the laptop control HID handle
and the authoritative hardware writer throughout phase 1. WinUI 3 and the
Session Agent keep their current roles. Any later call to a pure helper supplies
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
memory. Profile merging remains a later, separately reviewed extension.

Use C linkage and the C calling convention (`extern "C"` / C# `Cdecl`). Export
`ob_abi_version()` returning `uint32_t` value `0x00010000` for ABI 1.0. Every work
export has a versioned name (`ob_v1_encode_report`,
`ob_v1_validate_response`) and an initial
`uint32_t abi_version` argument. Require an exact supported version before
reading operation inputs. A missing export, wrong architecture, incompatible
version or layout fails the helper admission with a visible diagnostic before
hardware work. Never guess a layout or silently switch implementations in a
live hardware operation. The later integration PR decides startup selection and
recovery behavior explicitly.

Use these representation rules:

- Integers are `uint8_t`, `uint16_t`, `uint32_t` or `int32_t`; lengths and
  capacities are `uint32_t` byte counts. Never expose Rust enums, `bool`,
  `usize`, managed objects, platform `long`, strings or collections directly.
- Boolean values are `uint8_t` 0/1. Optional fields have a separate `uint8_t`
  presence flag, also 0/1. Reject other flags and unknown enum values at the ABI
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
- The future profile helper (not exported in stage 4) will take a required baseline and optional override with
  explicit presence for each projected field, including fan-control/display
  objects. Its v1 slice contains performance, legacy fan mode, fan-control mode
  and fixed RPM, display keys/HDR/brightness, and gaming-mode enabled. Curves
  remain host-owned unchanged values; this slice can select the existing
  default curve when legacy fan settings require it. Strings remain opaque keys,
  not OS lookups. The exact record layout and host reconstruction of untouched
  fields must be reviewed with the first binding, against the shared fixtures.

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
A later production host adapter will map category/detail to the existing taxonomy, preserving
validation failure and submission state. A pure-helper failure never means a
hardware command was submitted, and cannot reinterpret an earlier ambiguous
submission as safe to retry. Fixture `payload-too-large` maps to invalid-argument
detail 1; `destination-length` maps to detail 2 for the current C# exact-span
API; `baseline-null` maps to detail 5. The codec ABI's capacity-based output
buffer rule is separately tested and does not change C#'s exact-span contract.

Contain panics inside every work export with an unwind-catching boundary;
validate and compute into bounded temporary storage before publishing output.
No panic or C++/managed exception may unwind across the ABI. The later build
must support catching Rust unwinds; aborts, allocation failure or process faults
cannot be converted into recoverable errors and must not be described as such.
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
production activation policy. Stage 5 must separately review acquisition,
packaging, architecture selection and trusted production loading.

`OpenBlade.Native.slnx` and `rust/Test-NativeInterop.ps1` are explicit developer
paths. The ordinary product solution does not reference these projects or build
Rust. Tests require `OPENBLADE_NATIVE_LIBRARY` to name the actual built artifact;
a missing artifact fails. Rust unit tests inject a panic privately in the same
exported work boundary; production libraries expose no panic/test entrypoint.
C header smoke tests link and call the real shared library. ABI capacity tests
are distinct from the canonical C# exact-destination-span error contract: a
91-byte ABI output capacity succeeds and writes only 90 bytes.

## Shared parity baseline

[The fixture contract](../tests/Fixtures/Parity/README.md) defines the exact JSON
consumed by C# and the stage 3 Rust report tests. Rust profile tests remain stage 6.
It pins the reviewed source revision and independent expected values. These are
synthetic software vectors, separate from production capture/admission evidence.
Existing tests remain in place.

Later stages must run both readers against these same files before replacing a
helper. Full profile validation, application selection, override lifetimes,
hardware admission, transport outcomes, ownership, durable recovery and
platform behavior need their existing tests plus the focused parity tests for
the slice actually migrated. This PR does not start those later stages.

## Migration stage reviews

Each migration stage requires a real Claude advisory review of its exact scope.
Record the reviewed base and head commits. Codex validates the findings against
that revision, fixes supported issues, and verifies the affected checks. Record
the findings disposition and resulting head in the stage's PR. Attribute the
review to the revision Claude actually inspected; a later fix commit does not
inherit that review. This requirement applies to the Rust migration stages.
