# Linux and macOS discovery pilot

`openblade-discovery` is a developer tool for one metadata snapshot. It is a
separate Cargo workspace outside the Windows native source manifest. Windows
product packaging excludes the binary.
The [Windows status CLI](../../rust/crates/openblade-status-cli/README.md) remains
a separate named-pipe client.

From the repository root, using the pinned Rust toolchain under `rust/`:

```sh
cd rust
cargo test --locked --manifest-path ../tools/openblade-discovery/Cargo.toml
cargo run --locked --manifest-path ../tools/openblade-discovery/Cargo.toml
```

Run natively on Linux x64 (`x86_64-unknown-linux-gnu`), Intel macOS
(`x86_64-apple-darwin`), or Apple silicon macOS (`aarch64-apple-darwin`).
On macOS use the installed Xcode Command Line Tools or Xcode to link the
system frameworks. For an explicit Intel build and run on an Intel host:

```sh
cargo run --locked --manifest-path ../tools/openblade-discovery/Cargo.toml --target x86_64-apple-darwin
```

The CLI takes no arguments. It prints one JSON line on success (including
unavailable or incomplete discovery). Windows builds can test fixtures; a
live invocation there exits 1 with a fixed diagnostic and no JSON.

## Snapshot v1

The [synthetic snapshots](fixtures/) are inputs for native UI prototyping.
They have the same shape as the CLI output. No fixture is a hardware result.

- `platform`: `linux` or `macos`.
- `hostMetadata`: `razerBladeReported` for a Linux DMI vendor and Blade-like
  product name, `otherVendor` for another valid Linux DMI vendor,
  `unverified` for a macOS model string or insufficient Razer DMI product
  evidence, and `unavailable` for a missing or invalid host identity source.
  These describe what the OS reports. `exactModel` is always JSON null.
- `hid.state`: `none`, `single`, or `ambiguous` describes the number of
  matching metadata entries in a completed scan. `incomplete` means the
  scan hit its bound, an entry could not be classified, or IOKit invalidated
  the iterator; rows may be partial. `unavailable` means the enumeration
  source could not be opened. Neither condition means a device is absent.
- `hid.candidates`: USB Razer entries grouped by numeric product ID and
  sorted by that ID. Each row contains numeric `vendorId` (5426, or 0x1532),
  `productId`, and `entries`. An entry is a Linux HID device or a macOS
  registered `IOHIDDevice` service. Several entries can belong to one
  physical unit. A row can also describe an external peripheral.

At most 256 entries contribute to the snapshot, with one additional
iteration to detect overflow. Host text is capped at 128 bytes and
discarded after classification. The tool does not collect serials, UUIDs,
device paths, descriptors, reports or names of attached devices. It
does not infer a model from a PID, firmware hints, or a platform name.
An Intel macOS installation on a Blade can report an Apple model. No
snapshot authorizes hardware access or establishes product support.

## Source evidence

- Linux [v6.18 DMI attributes](https://github.com/torvalds/linux/blob/v6.18/drivers/firmware/dmi-id.c)
  expose `sys_vendor` and `product_name` as read-only attributes under
  `/sys/class/dmi/id`. The pilot reads those two files only.
- Linux [v6.18 HID modalias](https://github.com/torvalds/linux/blob/v6.18/drivers/hid/hid-core.c#L2675-L2686)
  defines the read-only `modalias` attribute for HID entries in
  `/sys/bus/hid/devices`. The parser uses the bus, vendor and product fields,
  including [the BUS_USB constant](https://github.com/torvalds/linux/blob/v6.18/include/uapi/linux/input.h#L236-L240).
  It does not read `uevent`, which can include a unique identifier.
- macOS [XNU 12377.1.9](https://github.com/apple-oss-distributions/xnu/blob/xnu-12377.1.9/bsd/kern/kern_mib.c#L1013-L1016)
  registers `hw.model` as a read-only string. The adapter calls
  [`sysctlbyname`](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/sysctlbyname.3.html)
  with no write buffer.
- Apple [IOKitUser 100222.0.4](https://github.com/apple-oss-distributions/IOKitUser/blob/IOKitUser-100222.0.4/IOKitLib.h)
  documents registry matching, iterator validity and the ownership of returned
  objects and copied properties. [IOHIDFamily 2222.0.24](https://github.com/apple-oss-distributions/IOHIDFamily/blob/IOHIDFamily-2222.0.24/IOHIDFamily/IOHIDDevice.cpp#L935-L938)
  publishes transport and numeric IDs on `IOHIDDevice`; its
  [keys header](https://github.com/apple-oss-distributions/IOHIDFamily/blob/IOHIDFamily-2222.0.24/IOHIDFamily/IOHIDKeys.h)
  defines the USB transport value. The pinned [CoreFoundation number header](https://github.com/apple-oss-distributions/CF/blob/CF-1153.18/CFNumber.h)
  defines the typed conversion used to read integer IDs. The adapter copies only those properties
  as needed; it does not open a device or user client, subscribe to input,
  or request privileges.

## One physical check

On an already-working Blade running Linux or Intel macOS, run the command
above once as a normal user. Compare the candidate PID against the
[Windows catalog](../../src/OpenBlade.Contracts/RazerBladeModelCatalog.cs)
for the independently known model. For example, an RZ09-0328 (historical
candidate, not a tested host) that publishes one matching USB HID entry and
reports a valid macOS model string would produce:

```json
{"schemaVersion":1,"platform":"macos","hostMetadata":"unverified","exactModel":null,"hid":{"state":"single","candidates":[{"vendorId":5426,"productId":597,"entries":1}]}}
```

With more entries, expect `ambiguous` and compare the matching row's count;
on Linux, `razerBladeReported` is expected only when DMI reports both the
Razer vendor and a Blade-like name. Record the OS/architecture, independently
known model and sanitized JSON. If the expected PID is missing or the scan
is incomplete, record that result without elevating or probing devices.
Even a matching PID leaves exact identity, internal-device topology,
device access, and every firmware operation unverified. Hosted runner
checks establish software behavior only.
