# SO6120 MCU 1.07 to EC fan-control bridge

Static follow-up on 2026-11-05 establishes a code-level path from the normal
`1532:02E0` controller to the EC's banked registers. The ordinary fan commands
use this path for mode, manual state and RPM requests. A separate, gated generic
register interface can reach the fields used by the EC's direct-duty branch.

This resolves the missing transport link from the [EC 1.04 analysis](so6120-ec104-fan-analysis.md).
It does **not** establish a supported, thermally supervised fan-off operation.
The normal duty-cycle command is rejected by this MCU, and the generic interface
does not supply fan-specific bounds, exclusive ownership or exact restoration.
No hardware experiment, production control or other-model admission follows
from this analysis.

## Exact inputs and method

| Artifact | Identity |
|---|---|
| Local signed carrier | `SO6120_MCUv01.07.00.exe`, 1,387,888 bytes |
| Carrier SHA-256 | `252618AE4CB3C865DE557D198AC3B7CF1310865E529EB1DAAB9790675792FEB7` |
| Signature | Windows Authenticode Valid; Razer USA Ltd. |
| Payload | `SO6120_keyboard_FW_v1.07.00_20260810.hex`, 162,003 bytes |
| HEX SHA-256 | `1306B80533EE78E82BBCDB91AB646F650A511AF379EF36AD3662A1E149782386` |
| Decoded image | 57,764 bytes, base `0`, unspecified gaps filled with `FF` |
| Image SHA-256 | `90BBE0D8DD1CA31614A2465C7C8821347B2A667606C39BEDADBD59B98886344F` |

The NSIS carrier was decompressed as data. All 3,603 Intel HEX records passed
length, framing and checksum verification; 57,576 populated bytes matched the
decoded image. Package configuration specifies `RZ09-0581` and `MCUVER=01.07.00`.
Its packaged update command binds the image to `1532:02E0`; it was inspected,
never executed. The MCU USB descriptor at `0xDDB4` independently contains that
VID/PID. ARM Thumb vectors identify initial stack `0x100013A8` and reset `0x19D`.

Ghidra 12.1.3 was used only for static analysis. Flash was marked read-only and
peripheral analysis blocks volatile; key branches and constants were checked
against instructions and bytes. Some recovered switch expressions remain
incomplete, so their tables were decoded directly. No updater or vendor code
was run, no device was accessed, and no installed files or settings changed.
Full analysis artifacts remain ignored under `artifacts/silent-mcu-research`
and `artifacts/silent-firmware-research`; vendor payloads are not committed.

## Ordinary fan commands reach the EC clamp

The MCU command-class dispatcher starts at `0x9598`. Class `0D` calls thermal
handler `0x96D4` at `0x961C`. That handler updates a cache at `0x10000220` and
queues work through the pending mask at `0x100000C8`.

| Host operation | MCU worker operation | EC bank-0 destination |
|---|---|---|
| Fan 1/2 RPM request (`0D/01`) | Worker `0x4B5C` writes indices `1B/1C` | `0x80330/0x80331` |
| Mode (`0D/02`) | Writes index `1A`, then mailbox index `0A` with value 5 | Mode `0x8035F`; mailbox `0x80940` |
| Queued manual state | Writes index `36` | Flags `0x8034D`, including manual bit 0 |

The mode path accepts thermal/channel identifiers 0/1/2 in payload byte 1;
these are not USB command-class IDs. The mode value is payload byte 2.
Instructions `0x97A4..0x97A6` reject modes above 7, including `FE` and `FF`.

The worker's write calls occur at `0x4D56` (mode), `0x4D6C` (mailbox),
`0x4C78` (manual state), `0x4DE4/0x4E04` (RPM). Read/write helpers
`0x5864/0x58B0` use address bytes `B0/B1` and call I2C transport `0x2520`.
This matches the EC's configured slave-address register value `58` and its
banked receive/transmit routine `0x249C`.

Thus the ordinary targets feed the same EC policy that clamps Silent manual
requests to its minimum and gives selector 3 a below-minimum zero exception.
The normal worker does not write the direct-duty fields described below.

Host mode/target GETs return MCU cached state; they are not synchronous EC
register reads inside the GET handler. Separate worker reads refresh that
cache. Consequently, a matching requested-target response alone cannot prove
the later EC bus write completed. This qualifies what the previous stored-target
observations establish; their independently observed fan rotation is unchanged.

## The named duty API is rejected here

The pinned Product 736 base-device JavaScript exports
`getThermalFanDutyCycle` and `setThermalFanDutyCycle`. Their actual module
`13546` implementations use GET `0D/8D` and SET `0D/0D`, with class, fan ID and
percentage fields; the setter checks percentage 0–100. Source offsets are
approximately 68,363 and 68,786 in `rzDevice25.bcaf2d44cf6bcb983c6f.js`.

The same file contains named constants suggesting `0D/8E` and `0D/0E`, but
those constants are not used by these wrapper implementations. Following the
actual function is necessary: `0D/0E` has a different implementation in this
MCU. Main and policy bundles contain no calls to the duty wrappers.

In MCU 1.07, SET `0D/0D` reaches the status-5 unsupported branch at `0x98A8`.
GET `0D/8D` also reaches it: the switch count is at `0x972E`, the table begins
at `0x972F`, and entry 8 contains `BD`, resolving to `0x98A8`. Both results
were checked in instructions rather than inferred from incomplete pseudocode.
Sending the named duty command therefore supplies no established bypass.

## Generic register access closes the transport link

The class-00 handler at `0x7164` contains a separate access gate and EC
read/write path. Gate SET `00/22` stores a boolean in MCU RAM `0x100000C1`;
GET `00/A2` returns it. This is separate from the EC's own duty permission bit.
MCU initialization `0x4AEC` clears the gate, but no bounded lease or host-loss
expiry was established.

With that gate set, generic read/write handlers `00/A1` and `00/21` call the
same EC I2C helpers at `0x7460/0x7470`. Instruction argument setup establishes
the register index, transfer length, data area and returned bus status. These
are broad transfers; no fan-specific range check or explicit payload-length
bound was found in the examined handler/helper path. This report does not
provide a hardware test sequence or expose the interface in OpenBlade.

The EC maintains eight banks of 256 mapped entries. Each entry contains
metadata and a target pointer; metadata value 1 is read-only, while the
following control entries carry value 2. The actual table bytes and both EC
bridge implementations were checked independently.

| Bank-0 field | EC target | Static purpose |
|---|---|---|
| Mode `1A` | RAM `0x8035F` | Normal selector or internal direct-duty selector |
| Manual flags `36` | RAM `0x8034D` | Includes normal manual enable |
| Duty requests `B8/B9` | RAM `0x80394/0x80395` | Raw duty bytes consumed by the `FE` branch |
| Permission flags `F0` | RAM `0x80425` | Bit 6 admits that branch; other bits have other uses |
| Duty readback `B0/B1` | MMIO `0xF01807/0xF01803` | Actual duty registers; mapped read-only |
| Bank selection `0F` | RAM `0x8144C` | Shared bank state |

EC SMBus instructions `0x24F6..0x24FA` load and transmit the mapped byte;
`0x2576` writes permitted mapped entries. The first transmit MMIO store was
lost in the initial pseudocode as an overwritten store, so the assembly is
the relevant evidence. A separate BRAM mailbox also accesses this mapping.
The EC's `1532:F003` USB descriptor therefore does not imply these fields are
isolated from the normal `1532:02E0` MCU.

## Why this is not yet a fan-stop capability

- The EC `FE` path copies raw duty bytes and skips the ordinary curve/minimum
  path. Periodic routine `0x1586A` also suppresses the normal adjustment loop
  for `FE`. Coverage by other thermal protections has not been established;
  the analysis does not claim all hardware emergency protection is disabled.
- `FE` is a separate internal selector. Preservation of Silent's complete
  performance/power behavior is not established by reaching its duty fields.
- Enabling the MCU register gate does not pause the ordinary queued worker.
  That worker does not consult the gate and assumes the current EC bank has
  the normal mapping. Gate access does not grant exclusive thermal ownership.
- Ordinary GETs can return cached mode/targets, while raw operations change
  EC fields directly. Request readback, actual duty and actual tachometer RPM
  must remain distinct. Cache reconciliation and pending work matter during
  entry and restoration.
- Permission clearing selects a normal firmware policy; it is not an exact
  saved-state restore. Power-state routines clear flags, but no verified
  atomic handback, disconnect watchdog or complete restoration contract was
  found for this direct-duty path.

The supported conclusion is **static reachability through a generic register
bridge**, with a rejected normal duty API. A review of protection, ownership
and exact recovery would be needed before proposing a new typed reversible
experiment. No such experiment was implemented or run. Production fan-stop
admission remains withheld, other models are not inferred from this image,
and the live Silent Automatic comparison remains paused.

Independent review confirmed the input identities, HEX decoding, USB identity,
normal transfer path, rejected duty commands, generic gate/transfer arguments
and EC bank mapping. It did not validate physical behavior or a deployable
fan-stop control.
