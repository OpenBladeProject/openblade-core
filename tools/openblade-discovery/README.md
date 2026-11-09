# macOS discovery pilot

`openblade-discovery` is a developer tool for one macOS metadata snapshot.
It has a separate Cargo workspace outside the Windows native source manifest.
Windows product packaging excludes the binary.

Use Xcode or the Xcode Command Line Tools to link the system frameworks.
From the repository root, run on Intel macOS:

```sh
cd rust
cargo test --locked --manifest-path ../tools/openblade-discovery/Cargo.toml --target x86_64-apple-darwin
cargo run --locked --manifest-path ../tools/openblade-discovery/Cargo.toml --target x86_64-apple-darwin
```

On Apple silicon, substitute `aarch64-apple-darwin` in both commands.
The CLI takes no arguments. It prints one JSON line on success, including
unavailable or incomplete discovery. Live discovery is macOS only.

## Snapshot v1

The [synthetic snapshot](fixtures/macos-unverified.json) illustrates the same
shape as live output. It is test data.

- `platform` is `macos`. `hostMetadata` is `unverified` for a readable,
  valid OS model string, or `unavailable` when the read fails or is invalid.
  `exactModel` is always JSON null.
- `hid.state`: `none`, `single`, or `ambiguous` describes the number of
  matching registry entries in a completed scan. `incomplete` means the
  scan hit its bound, an entry could not be classified, or IOKit invalidated
  the iterator; rows may be partial. `unavailable` means the registry
  enumeration could not start. Neither condition implies device absence.
- `hid.candidates`: USB Razer entries grouped by numeric product ID and
  sorted by that ID. Each row contains numeric `vendorId` (5426, or 0x1532),
  `productId`, and `entries`. One physical unit can have multiple
  registered `IOHIDDevice` services, and a row can describe an external
  peripheral.

At most 256 entries contribute to the snapshot, with one additional
iteration to detect overflow. Host text is capped at 128 bytes and
discarded after classification. The tool does not collect serials, UUIDs,
registry paths, descriptors, reports, or names of attached devices. An
Intel macOS installation on a Blade can report an Apple model. Neither
that name nor a matching PID establishes exact identity or write authority.

## Source evidence

- Apple [XNU 12377.1.9](https://github.com/apple-oss-distributions/xnu/blob/xnu-12377.1.9/bsd/kern/kern_mib.c#L1013-L1016)
  registers `hw.model` as a read-only string. The adapter calls
  [`sysctlbyname`](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/sysctlbyname.3.html)
  with no write buffer.
- Apple [IOKitUser 100222.0.4](https://github.com/apple-oss-distributions/IOKitUser/blob/IOKitUser-100222.0.4/IOKitLib.h)
  documents registry matching, iterator validity and ownership of returned
  objects and copied properties. [IOHIDFamily 2222.0.24](https://github.com/apple-oss-distributions/IOHIDFamily/blob/IOHIDFamily-2222.0.24/IOHIDFamily/IOHIDDevice.cpp#L935-L938)
  publishes transport and numeric IDs on `IOHIDDevice`; its
  [keys header](https://github.com/apple-oss-distributions/IOHIDFamily/blob/IOHIDFamily-2222.0.24/IOHIDFamily/IOHIDKeys.h)
  defines the USB transport value. The pinned [CoreFoundation number header](https://github.com/apple-oss-distributions/CF/blob/CF-1153.18/CFNumber.h)
  defines the integer conversion. The adapter copies only the selected
  properties. It does not open a HID device or user client, subscribe to
  input, or request privileges.

## One physical check

On an already-working Intel macOS Blade, run the command above once as a
normal user. Compare the candidate PID with the
[Windows model catalog](../../src/OpenBlade.Contracts/RazerBladeModelCatalog.cs)
for the independently known model. For example, an RZ09-0328 (historical
candidate, not a tested host) that publishes one matching USB HID entry and
reports a valid macOS model string would produce:

```json
{"schemaVersion":1,"platform":"macos","hostMetadata":"unverified","exactModel":null,"hid":{"state":"single","candidates":[{"vendorId":5426,"productId":597,"entries":1}]}}
```

With more entries, expect `ambiguous` and compare the matching row's count.
Record the OS/architecture, independently known model and sanitized JSON.
If the PID is missing or the scan is incomplete, record that result
without elevating or probing devices. A match leaves exact identity,
internal-device topology, device access and all firmware operations
unverified. Hosted runners establish software behavior only.
