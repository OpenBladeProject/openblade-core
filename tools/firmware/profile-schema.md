# Offline profile and report schema 1

Profiles describe exact firmware images. They are reviewable metadata, not
executable plugins or runtime hardware admission. The parser rejects unknown
profile fields and decoder names. All addresses and file offsets are JSON
integers; hashes are 64 lowercase hexadecimal characters.

## Minimal profile

Replace every example identity and the image hash/size before use:

```json
{
  "schema_version": 1,
  "id": "example-blade-ec-1.00",
  "label": "Example Blade EC 1.00",
  "model": "RZ09-EXAMPLE",
  "component": "EC",
  "version": "1.00",
  "architecture": "Reviewed processor architecture",
  "endianness": "little",
  "image": {
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "size": 1,
    "base_address": 0
  },
  "carriers": [],
  "annotations": [],
  "findings": []
}
```

IDs use lowercase letters, digits, dots, underscores and hyphens. An image's
load base plus size must fit the 32-bit address space. A raw image's base comes
from its hash-matched profile; Intel HEX supplies its own base, which must match.
An unknown raw image is described at file base zero with no interpreted fields.

## Optional bank map

The generic bank-map decoder handles a table of pointers to fixed-size entry
arrays. Pointer values are image addresses; subtracting `image.base_address`
locates the array in the file. Target register addresses are preserved as
runtime addresses and are never dereferenced as host memory.

```json
{
  "pointer_table_offset": 199640,
  "bank_count": 8,
  "entries_per_bank": 256,
  "entry_size": 8,
  "metadata_offset": 0,
  "metadata_size": 4,
  "address_offset": 4,
  "address_size": 4,
  "byte_order": "little",
  "access_values": {"1": "read-only", "2": "read-write"}
}
```

This example is SO6120 geometry, not a default for another Blade. Address size
also determines pointer size and supports 1, 2 or 4 bytes. Metadata has its own
size (1–8 bytes). The fields must not overlap or exceed an entry. The engine
rejects out-of-image, duplicate and overlapping bank tables. It supports at
most 256 banks, 4,096 entries per bank and 32,768 total entries. Unannotated
entries retain raw metadata and default to unclassified byte registers.

## Register annotations and references

An annotation requires `address` and `width_bits` (8, 16 or 32). For banked
images, `bank` and `index` are required and must match the decoded mapping.
For an explicit internal field without a bank map, both are null or absent.
Its optional `access` must agree with the map if specified; an annotation
cannot change read-only metadata into writable access.

Optional strings are `name`, `category`, `description`, `access`, and `units`.
Optional lists are `notes` (strings), `references`, and `bits`. Missing text
and lists normalize to empty values. `evidence_status` is one of
`firmware-confirmed`, `inferred`, `physically-verified`, or `unknown`; the
default is `unknown`. There is no automatic promotion to physical validation.

Each bit object has `bit` (zero-based, within width), optional `name` and
`description`, its own `evidence_status`, and `references`. Duplicate bit
numbers are rejected. References have this shape:

```json
{
  "kind": "instruction",
  "source": "Exact image and version",
  "address": 90670,
  "label": "Condition and affected field"
}
```

`kind` is `instruction`, `table`, or `document`. `address` can be null.
References are displayed as text, never loaded or executed. Findings contain
`title`, `description`, `evidence_status` and `references`.

## Optional carrier and thermal definitions

Each carrier requires its `sha256`, `size`, `format` (`nsis-blocks-v1`),
zero-based `block_index`, `payload_format` (`binary` or `intel-hex`) and
`payload_sha256`. The whole carrier must match before extraction, the payload
must match after extraction, and the decoded image must match the profile.
Package filenames alone do not select a decoder.

The optional `thermal_tables` object requires `decoder`, `group_offsets`,
`mode_count`, `table_size` and `mode_names`. Currently only
`so6120-thermal-v1` is implemented: tables are 512 bytes, groups do not overlap,
and all stages/extensions must fit. `mode_names` maps decimal mode strings to
reviewed labels. Declaring a decoder for another image requires evidence that
its layout and units match; model similarity is insufficient.

## Inspection reports and comparison

`report.json` has `schema_version: 1`, `kind: "firmware-inspection"`,
`offline: true`, `tool_version`, `profile` (metadata or null), `source`, `image`,
`registers`, `thermal_tables`, `findings` and `warnings`. It contains no live
readings. `source` records basename, size, SHA-256 and input format; `image`
records size, SHA-256, load base and populated-byte count.

Each register combines normalized annotations with `key`, `metadata` and
`table_offset`. Keys are `b<bank>:i<index>` for banked fields or `a<address>`
for explicit fields, using decimal integers. The address field is always a
runtime target; `table_offset` is a file offset. Neither is a measured value.

Thermal rows preserve group, mode, file offset, table hash, channel identities,
decoded target RPM, raw sensor-bound pairs, uninterpreted stage-tail bytes,
and manual minimum/maximum RPM. The HTML labels sensor values as raw units.

Render and diff validate report structure, ranges and identities before use.
A report is a portable analysis record, not a signed attestation: rendering a
supplied JSON file does not independently verify its claimed source image.
The diff retains both profiles/image hashes and records changed, added and
removed register/table rows. Cross-profile differences require interpretation;
equal numeric keys alone do not establish equal hardware semantics.
