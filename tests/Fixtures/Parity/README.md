# Shared parity fixtures

`v1/*.json` is the language-neutral migration baseline. C# test projects copy
these files into their `Parity/` output folder. Rust report tests read the same
repository report file directly, with no generated expectations or duplicated
copies. The 14 profile cases await the stage 6 Rust implementation.

Each file is UTF-8 JSON with LF line endings, `schemaVersion: 1`, a `suite`, a
`sourceRevision` matching the v1 provenance revision below, and named case arrays.
Field names are case-sensitive; unknown/duplicate fields and numeric strings are rejected.
Override projection fields may be omitted as documented below; envelope and row
fields are required even when their value is null. IDs are unique within each array and
appear in test names and mismatch messages. Reject unsupported schema versions
and suites. Adding cases preserves v1; changing field meanings requires a new
schema directory and readers. These fixture versions are independent of ABI,
IPC, saved-profile, journal and product versions.

Hex strings contain complete bytes in uppercase, two characters per byte,
without separators or a prefix. Integers are JSON integers; there is no base64,
native-endian struct, .NET type name or serializer-specific enum encoding.
All expected values are checked-in literals. Tests never derive expected bytes
or policy results by calling the production implementation.

## Report suite

`razer-report.json` has `suite: "razer-report"`. `encoding` rows contain a
`request` (transaction/class/ID integers 0..255 and `payloadHex`), a
`destinationLength`, `expectedError` and `expectedReportHex`. Success has a null
error and all 90 expected bytes. Errors `payload-too-large` and
`destination-length` map to C# `ArgumentOutOfRangeException` and
`ArgumentException`; rejected encodings leave the prefilled destination intact.
The tests exercise allocating, instance-span and static-span encoders.

`responses` rows contain the same request plus `responseHex`, nullable
`expectedPayloadLength`, `expectedFailure` and `expectedPayloadHex`. Success has
a null failure and a copied payload. A failure is `{ "name": ..., "value": ... }`
using the existing `HidProtocolValidationFailure` name and numeric value. The
tests verify both, without comparing localized exception text. Several rows
contain multiple faults to pin validation precedence, including payload size
before destination length in encoding. Theory arguments contain only IDs;
the test-only reader looks up and validates each row from the same file.

Provenance at `123936f3ea69881d21bd16a7a09529930c9a2c6f`:

- `tests/OpenBlade.Protocol.Tests/RazerReportTests.cs:28-79` anchors the known
  balanced request, prefix/checksum, payload limit, transaction and expected
  payload-length failures. `CommandCatalogTests.cs:38-48,136-146` independently
  anchors the command payload. This file adds no command to that catalog.
- `src/OpenBlade.Protocol/RazerReport.cs:55-226` defines the 90/80-byte limits,
  zero-filled envelope, checksum XOR over offsets 2..87, response status and
  validation order. Complete literal vectors were assembled independently
  from that byte layout, including empty/80/81-byte boundaries and faults with
  otherwise coherent checksums. The known request's checksum is `0B`.
- `src/OpenBlade.Protocol/HidSessionResult.cs:31-42` anchors error numbers.

These vectors exclude Windows' leading report-ID byte and do not test native
transfer sizes, HID identity, transaction allocation, payload semantics or
hardware state. Arbitrary header/payload boundary bytes are synthetic envelope
inputs, not newly admitted firmware commands. The existing command/evidence
tests remain the authority for admission.

## Profile-policy suite

`profile-policy.json` has `suite: "application-profile-merge"` and `cases`.
Each row supplies `baseline`, `override`, `expected` and `expectedError`.
The baseline and expected projection contain:

| Field | Representation |
| --- | --- |
| `performance` | Existing numeric `PerformancePreset`: Silent 0, Balanced 1, Performance 2, BatterySaver 3, BatteryBalanced 4, Custom 5, Turbo 6, MaximumPower 7, Gaming 8, Creator 9 |
| `fans` | Existing numeric `FanMode`: Automatic 0, Fixed 1, ManualCurve 2, Maximum 3 |
| `fanControl` | null or `{ "mode": <FanMode>, "fixedSpeedRpm": <integer> }`; curve is the unchanged host default, outside this projection |
| `display` | null or an object with nullable `refreshRateKey`, `hdrEnabled`, `colorProfileKey`, `brightnessPercent` (0..100) |
| `gamingModeEnabled` | nullable Boolean |

A null override means no application override. In a present override object,
omitted or null fields inherit baseline values; false and zero are explicit
values. Display keys are opaque strings, including UTF-8, with no path or OS
meaning. A present override normalizes an absent baseline display to an
unconfigured display. A null baseline is the sole malformed policy row and
expects `baseline-null` (`ArgumentNullException`).

Provenance at the same pinned revision:

- `src/OpenBlade.Service/ApplicationProfileResolution.cs:5-58` defines this
  exact merge seam: nullable inheritance, legacy/new fan selection, fieldwise
  display merge and gaming-mode override.
- `src/OpenBlade.Contracts/Profiles.cs:3-15,194-203,348-384,513-590,629-641`
  supplies enum numbers, legacy fixed speed 3700 RPM, Maximum 5400 RPM and
  nullable fields. Projection expectations were reviewed directly against
  those rules, rather than recorded from a production test run.
- `tests/OpenBlade.Service.Tests/ApplicationProfileAutomationTests.cs:8-80`
  provides existing application override/priority and gaming-mode test anchors.

The C# adapter builds typed values and calls the existing merge functions; it
also compares the complete result to preserve fields outside the projection.
The cases cover sparse inheritance, false/zero, maximum brightness, UTF-8,
legacy fan defaults and explicit fan-control precedence. The empty override
case targets the pure merge function; transport validation may reject an empty
application override. The conflicting fan-fields case records merge precedence
before whole-profile validation; it preserves both raw fields and does not claim
which mode downstream consumers use. Whole-profile validation rejects that
contradictory pair. Separate cases preserve the accepted legacy Maximum plus
Fixed/5400-RPM compatibility shape (`ProfileSetValidator.cs:402-411`). These are
not hardware-admission fixtures.
Merge is not full profile validation, normalization,
capability admission, foreground selection, power-source selection, manual
override priority, journal recovery or hardware execution. Those existing
contracts and tests stay authoritative. Future migrated slices need their own
shared cases rather than inferring full-domain parity from this small suite.
In particular, overriding GamingModeKeyPolicy, CustomPerformance, GpuClockTuning
or Lighting is outside this projection; their override branches remain C#-owned
until a later slice adds its own parity cases.
