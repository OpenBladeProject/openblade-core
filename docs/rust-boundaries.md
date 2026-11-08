# Shared Rust core boundaries

This is the design contract for the first migration stage. It adds portable
parity tests while keeping production C# behavior unchanged. There is no Cargo
workspace, native loading, or runtime handoff in this stage.

## Ownership and dependencies

The future `rust/` Cargo workspace has four initial crates:

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

## Proposed C ABI v1

This contract is a proposal for the later implementation PR, not an exported API
in this PR. The first binding must provide the reviewed header, matching Rust/C#
layout tests and native failure tests before production use. Initial calls are
stateless, synchronous helpers: encode a report, validate a response, and merge
the bounded profile slice. They perform no I/O and retain no handles, callbacks,
tasks, global policy state or caller memory.

Use C linkage and the C calling convention (`extern "C"` / C# `Cdecl`). Export
`ob_abi_version()` returning `uint32_t` value `0x00010000` for ABI 1.0. Every work
export has a versioned name (`ob_v1_encode_report`,
`ob_v1_validate_response`, `ob_v1_merge_profile_slice`) and an initial
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
  the caller guarantees valid accessible pointers for each declared range.
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
- The profile helper takes a required baseline and optional override with
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
Hosts copy any result needed after releasing their pins.

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
C# maps category/detail to its existing exception/result taxonomy, preserving
validation failure and submission state. A pure-helper failure never means a
hardware command was submitted, and cannot reinterpret an earlier ambiguous
submission as safe to retry. Fixture `payload-too-large` maps to invalid-argument
detail 1; `destination-length` maps to detail 2 for the current C# exact-span
API; `baseline-null` maps to detail 5. The future ABI's capacity-based output
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

## Shared parity baseline

[The fixture contract](../tests/Fixtures/Parity/README.md) defines the exact JSON
consumed by C# now and by Rust later. It pins the reviewed source revision and
independent expected values. These are synthetic software vectors, separate from
production capture/admission evidence. Existing tests remain in place.

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
