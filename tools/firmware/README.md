# Blade firmware inspector

A developer-only, offline CLI and self-contained HTML register explorer.
Python 3.11 or later is the only runtime dependency for the CLI. No package
installation, service, driver, network connection or Ghidra installation is
needed to inspect the supported images. The tool never opens a device, executes
an updater or writes firmware.

Profiles cover the exact RZ09-0581 SO6120 EC 1.04 and MCU 1.07 images
reviewed in [the register investigation](../../docs/so6120-ec-register-semantics.md).
The EC profile decodes all eight banks (2,048 entries) and 32 factory thermal
tables. Reviewed annotations identify 28 entries, including the bank selector
in each bank. The MCU profile documents 16 reviewed internal fields. These are
offline analysis profiles, not additions to OpenBlade's supported-device catalog.

Additional profiles decode independently reviewed eight-bank maps and fan registers:

- [RZ09-0528 SO6110 EC 1.09](../../docs/fan-stop-so6110-source-review.md).
- [RZ09-0483 SO690 EC 2.01](../../docs/fan-stop-so690-source-review.md).
- [RZ09-0483 Lamborghini SO695 EC 1.01](../../docs/fan-stop-so695-source-review.md), including its board identifier.
- [RZ09-0510 SO6100 EC 1.02](../../docs/fan-stop-so6100-source-review.md).
- [RZ09-0530 PI4110 EC 1.03](../../docs/fan-stop-pi4110-source-review.md).
- [RZ09-0421 CH580 EC 1.03](../../docs/fan-stop-ch580-source-review.md).
- [RZ09-0427 PI480 EC 1.03 and 1.06](../../docs/fan-stop-pi480-source-review.md).
- [RZ09-0330 CH551 EC 1.01](../../docs/fan-stop-ch551-source-review.md).

Their thermal-table formats are not inferred from SO6120.

Further investigations cover the [Blade 17/18 conditional readback design](../../docs/fan-stop-blade18-readback-resolution.md)
and missing EC evidence for [Blade 14](../../docs/fan-stop-blade14-missing-ec-evidence.md)
and [Blade 15](../../docs/fan-stop-blade15-missing-ec-evidence.md). The
[coverage matrix](../../docs/fan-stop-model-coverage.md) distinguishes implemented
support from remaining source and software work.

## Run it

From the repository root, using `python` or `py -3`:

```powershell
python tools/firmware/firmware.py profiles
python tools/firmware/firmware.py inspect C:/firmware/SO6120_ECv01.04.exe --output artifacts/ec104
python tools/firmware/firmware.py inspect C:/firmware/SO6120_MCUv01.07.00.exe --output artifacts/mcu107
```

Use an existing parent directory and a new output directory. The supplied paths
are examples; firmware packages are not included in the repository. Open the
generated `index.html` in a browser. It has no remote fonts, scripts or assets
and works as a local file without a web server.

The explorer includes searchable, sortable, paginated registers; filters for
bank, access, evidence and category; register details with bit definitions and
instruction references; factory thermal tables; and image provenance. It
initially shows annotated entries when available. Clear filters to inspect the
full map. No live register values or write controls are present.

`report.json` is the versioned, deterministic machine-readable result. Known
carriers and Intel HEX inputs also produce `image.bin`. Raw binary inspection
does not make an extra image copy. Source filenames are recorded without local
directory paths; files retain their SHA-256 identities.

```powershell
python tools/firmware/firmware.py inspect C:/firmware/EC.bin --profile so6120-ec-1.04 --output artifacts/ec-bin
python tools/firmware/firmware.py render artifacts/ec104/report.json --output artifacts/ec104-copy.html
python tools/firmware/firmware.py diff artifacts/ec104/report.json artifacts/ec-next/report.json --output artifacts/ec-diff.json
```

An explicit profile must match the image exactly. Unknown raw binary or valid
Intel HEX inputs receive metadata-only reports, with no inherited register
meanings. Unknown executable carriers are rejected. Every command refuses to
overwrite an existing output or an input file; there is no force option.
Exit code 0 means success; validation, input/output and usage failures return 2.

On POSIX, output publication requires a filesystem with hard-link support.
Use a local supported filesystem if a removable or network volume rejects the
operation. Generated files are owner-only by default; change permissions
explicitly when sharing them. Windows uses no-replace rename publication.

## What is decoded and what is annotated

The binary supplies bank pointers, entry metadata, target addresses and thermal
table bytes. Names, bit meanings and instruction references are reviewed profile
annotations. The tool does not discover arbitrary firmware functions, recover
original vendor source or turn a decompiler guess into a confirmed meaning.

Evidence labels distinguish `firmware-confirmed`, `inferred`,
`physically-verified` and `unknown`. The shipped profiles use static firmware
evidence; the reports do not claim live physical validation. Mapped read-write
access is a descriptor attribute, not permission or a safe command contract.
Unclassified entries remain visible, including unresolved metadata values.

Manual targets, automatic table targets and PWM registers have different units.
The SO6120 thermal decoder uses 100 RPM/count for manual limits and 50 RPM/count
for automatic targets. Sensor thresholds stay in raw units. Factory table
contents are not a dump of the running EC's active policy.

The SO6120 profile's hash matching is an offline interpretation boundary. It
does not create a production BIOS/firmware allowlist or grant hardware access.

## Add another Blade profile

Profiles are data-only JSON under `profiles/`. To develop a profile outside the
repository, pass `--profile-file` to `profiles` or `inspect`; the option can be
repeated. No Python module or executable plugin is loaded from a profile.

```powershell
python tools/firmware/firmware.py inspect C:/firmware/other-ec.bin --profile-file C:/research/other-blade.json --profile other-blade-ec-1.00 --output artifacts/other-ec
```

Start with the schema below and the existing profiles as format examples.
Review the new image independently before assigning meanings. Another Blade
must not inherit SO6120 addresses, table layouts or control behavior by name.

| Profile field | Purpose |
|---|---|
| `schema_version` | Currently 1; unknown versions are rejected. |
| `id`, `label`, `model`, `component`, `version` | Unique analysis identity and display metadata. |
| `architecture`, `endianness` | Analysis context; neither invokes a disassembler. |
| `image` | Exact lowercase SHA-256, byte size and load base address. |
| `carriers` | Optional exact package hashes/sizes, reviewed extractor, block index, payload format and payload hash. |
| `bank_map` | Optional pointer-table offset, bank/entry counts, entry geometry, metadata/address sizes, byte order and access-value mapping. |
| `thermal_tables` | Optional explicitly named decoder and its reviewed group/mode/table geometry. |
| `annotations` | Exact bank/index/address matches, names, units, bits, access, evidence and references. Without a bank map, explicit internal-field annotations are supported. |
| `findings` | Reviewed explanatory findings and their provenance. |

See [profile schema](profile-schema.md) for field shapes and invariants. Register
annotations cannot contradict a decoded target address or access metadata.
Duplicate profile identities, image/carrier hashes, register identities and bit
definitions are rejected. New geometries can reuse the generic bank-map engine;
a genuinely different binary layout needs a separately implemented and tested
decoder. The SO6120 thermal decoder is explicitly selected by its profile.

Package extraction currently supports the reviewed NSIS block/LZMA layout.
Intel HEX handling checks framing, checksums, record geometry, overlaps, address
span and EOF. Input size is bounded to 64 MiB, decoded images to 16 MiB, and LZMA
dictionaries to 64 MiB. Unknown executable formats are not guessed or executed.
Full-file hash matching and payload verification replace any need to launch the
signed updater. This CLI does not perform a new Authenticode trust check.

The separate Ghidra research scripts remain useful for instruction analysis and
reviewing a new profile. Automated Ghidra orchestration and new decoder families
are follow-up work; this CLI has no dependency on a local Ghidra project.

The [PWM writer census](../../docs/so6120-ec-pwm-writers.md) extends the direct-duty
recovery analysis. The [normal-mode handback](../../docs/so6120-direct-pwm-handback.md)
review distinguishes an explicit Automatic exit from cached UI state and full
experiment cleanup. A separate developer [Windows battery observer](../../docs/windows-battery-observation.md)
records optional OS battery data without service or raw-register access. It is
not part of this offline CLI and cannot confirm fan RPM or Silent mode.

## Development and validation

```powershell
python -m unittest discover -s tools/firmware/tests -v
node --test tools/firmware/tests/test_explorer.js
```

The Python suite uses synthetic carriers/images and a second model's independent
layout. The JavaScript tests exercise explorer behavior with a lightweight test
DOM; they are not visual browser validation. Node is needed only for that test
command, not for using the CLI or generated report. The dedicated workflow runs
these checks on Windows and Linux without downloading vendor firmware.

Private update packages, extracted firmware, full decompilation, generated
reports and personal paths belong under ignored `artifacts/` or outside the
repository. Commit reviewed profile metadata and synthetic tests only.
