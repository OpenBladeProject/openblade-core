# SO6120 secondary mailbox ownership and completion

The BIOS `302/303` bulk-transfer candidate reaches a stored-record protocol
in the pinned EC 1.04 image. Its finite chunk count is useful new evidence,
but its busy flag is not an exclusive owner: another accepted `F0` can replace
the active descriptor. Cleanup resets transfer bookkeeping, not fan control.

The related write family can program persistent thermal tables. That is a
real fan-policy capability in the firmware, but it is not a reviewed runtime
PWM command or a safe replacement for the existing Auto-exit adapter.
No hardware command, live storage read, flash write or vendor-code execution
was performed. All findings are exact-image static evidence.

## Inputs and relationship to the BIOS client

The [BIOS investigation](so6120-acpi-vendor-transport.md#native-bios-ownership-and-completion)
pins the signed BIOS 4.01 carrier and `RazerAcpiDxe` client. Its secondary
indexes `12/13/14/15/19/1B` and `F0/F1/F2` sequence match the EC dispatcher
and fields below. The client copies data through host MMIO `FE0B8000`; the
EC-side read helper fills its transfer buffer at `84000`. This correspondence
is source evidence, not a live mapping or invocation test.

The EC image is 262,144 bytes with SHA-256
`8537e9fd55b7b35504d8d87b0191acc4391b67365b3fe72f6e1029a424c9e707`.
Addresses below are EC image offsets or explicitly named EC data addresses.
They are not Windows process addresses. Review reused the pinned disassembly,
checked critical instruction ordering and preserved original-image byte windows.
The decompiler omits some arguments and reorders shared statements; instruction
evidence is authoritative for those details.

This bulk protocol is distinct from the primary `93/94` mapped/raw mailbox
in the [host-mailbox contract](so6120-host-mailbox-contract.md). Its persistent
selector `D0` is not the primary raw-address `D0` operation. Other branches in
the secondary dispatcher, including the previously identified gated raw byte
writer, remain separate [PWM-writer evidence](so6120-ec-pwm-writers.md).

## Start and continuation do not acquire an owner

Dispatcher `0x12E2C` reads command `F02212`. The `F0` branch at
`0x12FB6..0x12FEE`:

1. Sets busy bit 0 at `F02219`, without first rejecting an already busy state.
2. Clears status `F0221B` and progress fields `804E0..804E2`, ORs read-active
   bit 0 into phase byte `8044F`, and sets counter `8044E` to `0x78`.
3. Calls selector handler `0x183D2`. A recognized selector reaches `0x18106`,
   replacing the active length at `80440` and cursor at `80444`.
4. Reaches command clear at `0x135EC` after handler return.

Thus a valid second start can displace an unfinished transfer. A busy-bit read
followed by publishing `F0` is not an atomic acquisition, and neither the flag
nor the response identifies a requesting host.

The `F1` instructions at `0x134C2..0x134E2` set phase bit 1, reload the counter
and clear ready bit 7 before checking continuation conditions. The checks at
`0x134E6..0x134F0` require read-active phase bit 0 and the previous status's
continuation bit 6; they do not check an owner or generation. Success calls
`0x18246` using the current shared cursor and remaining length. Rejection
stores `(oldStatus & 0x7F) | 0x81`. Both paths clear command; even rejected
continuation changes the shared state.

## Data selection and a conditional completion bound

`0x18226` searches 255 eight-byte directory entries at RAM `89000` for the
selector from `F02213`; `FF` and a missing selector fail. The directory is
loaded from persistent address `3FE000` by `0x1764E`. `0x18106` retrieves
the stored 16-bit length, checksum, metadata and location, overwriting the
host-facing metadata fields. The location formula is
`byte6 * 0x1000 + byte7 * 0x20`; storage reads add `0x400000`. This reader
does not dereference a host-supplied EC RAM address.

Read helper `0x1780E` calls `0x20066 -> 0x02B10`, which uses controller
address registers `F0103B..3D` and data register `F0103F`. It brackets the
read with `0x20D92/0x20DB0` (`setgie.d` / `setgie.e`). This temporarily disables
EC interrupts around the local read; it does not reserve the transaction across
the host's later requests. Interrupts are enabled again before the host consumes
or continues the transfer.

`F0` and `F1` clear the full `0x800`-byte buffer at `84000` before reading
each chunk. The clear helper's `0x200` argument counts DWORDs, not bytes.
Full chunks with more data subtract `0x800` from the remaining length and
advance the cursor by `0x800`; the final `F1` sets remaining length to zero
and clears continuation bit 6. For an isolated, stable record and returning
lower-level reads, this establishes at most **32 chunk responses** for any
16-bit length: one `F0` and at most 31 `F1` continuations. Zero length ends
in the initial response. This refines the BIOS-only finding of no local loop
counter; it is not a wall-clock deadline or a guarantee under replacement,
reset or interleaving.

Status bit 7 is set for errors as well as successful reads. `F0` uses
`0x1839C` to compare the stored checksum for a record of at most `0x800`
bytes. Its larger-record branch and the final `F1` do not perform that check.
Consequently, a ready multi-chunk response does not establish complete payload
integrity. Current directory contents and live data were not read.

## Cleanup and timer scope

`F2` calls `0x1819A` at `0x1350E`, then clears command at `0x135EC`.
The cleanup's complete direct store list zeros `F02214..1B`, `F02220..2F`
and `8044E..8044F`. It has no calls and no fan/PWM stores. It leaves the
selector `F02213`, active length/cursor, progress fields and transfer buffer
untouched. It does not restore an interrupted request or establish a new
response generation.

The scheduler-driven callback `0x1847C`, reached through `0xCBEC`, decrements
the nonzero counter. At zero, it calls the same cleanup only if command
`F02212` is already zero; otherwise it reloads the counter to one. The literal
`0x78` means 120 callback decrements, not a verified number of milliseconds.
This is conditional transfer cleanup, not a deadline independent of command
traffic or a host-loss cooling lease.

There is a real interaction with fan policy: the normal profile-change branch
of worker `0x15E46` checks this counter at `0x16318..0x1631C`. However, the
`8035F == FE` direct-duty branch splits earlier and can reach PWM stores at
`0x1628A/0x162BA` without that gate. Expiring the transfer counter therefore
does not establish return from direct duty to Automatic. The existing
[handback requirements](so6120-direct-pwm-handback.md) remain separate.

## Persistent fan-table programming is a different operation

The paired `F3/F4/F5` write family reaches erase/program and directory-update
operations. It has genuine thermal-policy reach:

| Selector | Established data and producer/consumer |
|---|---|
| `E0/E1` | Embedded DTT data vault, produced by `0x1D2AC`; all four 4,096-byte defaults match. Their leading length is 2,281 bytes, split into 2,048 and 233 bytes. |
| `E8` | 256-byte configuration produced by `0x184AC`, loaded at `87A00`; `0x1864E` uses threshold bytes to update state packed into `80207`. |
| `D0` | BIOS-consumed persistent record; its data identity remains unresolved. |
| `20..27` | Eight 512-byte thermal/fan tables seeded by `0x14D28`, loaded into `8A000` by `0x158FE`, with metadata `0C` and checksum validation. |

For `20..27`, write dispatcher `0x17B64` accepts up to 512 bytes and selects
fixed persistent locations `402000..402E00`. `0x17B50` calls `0x17A90` to
commit record metadata and `0x17934` to program the payload. These writes can
change fan policy; describing the entire secondary family as unrelated to fans
would be incorrect. They do not supply a volatile direct-duty transaction,
atomic durable restoration or an admitted Windows invocation path.

The recovered storage path therefore closes the specific BIOS bulk-transfer
question without enabling direct control. A live implementation still lacks
reviewed ownership across its actual participants, correlated completion,
independent state proof and restoration. No persistent table programming is
proposed as a substitute for the requested runtime fan/PWM control.
