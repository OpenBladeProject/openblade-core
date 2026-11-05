# SO6120 host mailbox ownership and completion

The offline follow-up resolves the vendor mailbox's software completion rule:
for an isolated mapped request with stable fields and no intervening lifecycle
reset, the command is cleared after the EC handler returns. That is useful
ordering, but neither the command nor status identifies the request or its
owner. The updater does not supply the exclusion needed to rely on that rule
when another actor can change the mailbox.

This is exact SO6120 evidence. It does not establish an admitted live transport,
physical fan behavior, or another model's command contract. The generic Auto
exit coordinator remains unbound to hardware.

## Inputs and verification

The [host mailbox investigation](so6120-host-mailbox-alternatives.md#exact-inputs-and-method)
pins the original carrier and extraction. This follow-up rechecks:

| Input | Size | SHA-256 |
|---|---:|---|
| EC 1.04 image | 262,144 | `8537e9fd55b7b35504d8d87b0191acc4391b67365b3fe72f6e1029a424c9e707` |
| Updater block 3 | 390,856 | `e3b25313595d1fd2a7c47fa46d6cbda45ae596e783eaa3fd771dafcf495d3358` |
| Bundled x64 InpOut library, block 11 | 98,304 | `5f27ed4d5cd58a1ee23deeb802e09e73f3a1d884ce2135f6e827f67b171269e7` |
| Embedded x64 driver, DLL offset `0x13BC0` | 15,008 | `f8965fdce668692c3785afa3559159f9a18287bc0d53abb21902895a8ecf221b` |

EC instruction spans and little-endian table words were checked against that
image. PEReader regenerated the updater metadata, including implementation
flags, signatures, direct callers and exception regions. All 269 instructions
of `RWIoI2C.I2C_Read_Write` match the earlier verified IL dump. Method offsets
below are IL offsets within the specified metadata token, not file addresses.
No vendor assembly, firmware or native driver was loaded or executed; there
was no hardware access. Raw inputs and working dumps remain ignored artifacts.

## Request publication and ownership

The updater publishes bank, index and count, then write data if applicable,
and finally command `A0` or `A1` at mailbox index `93`. Each mailbox-byte
access itself takes two native calls: write index to port `300`, then read or
write port `301` (`0600001F/20`). Command-last publication orders this caller's
stores; it does not make either the index/data pair or the request atomic.

A mutex does exist. `MainWindow..cctor` (`060000C2:0000..000B`) constructs
`Mutex(true, "Razer Update Tool")`; the constructor checks it with
`WaitOne(TimeSpan.Zero, true)` (`06000086:007B..008E`). These are the only
direct references to that mutex field. It is an application-instance mechanism,
not acquisition around a mailbox transaction. The reviewed port wrappers and
`I2C_Read_Write` have no synchronization implementation flag, managed lock,
idle acquisition, owner token or response generation. This IL does not
establish cooperation by other processes; the native path is reviewed below.

`EC_BANK` and `I2CBUF` are public static fields (`0400003C/3D`). Payload loops
access the shared buffer byte by byte; split requests reload the bank for the
second transaction. There is no immutable request snapshot. A short write
starting exactly at register `0F` changes the cached bank before submission
or success, even for zero count. Long writes and spans merely containing `0F`
do not perform that cache update. This is not a reusable bank-coherence rule.

The actual direct use is narrower than the generic helper. The only two direct
calls to `I2C_Read_Write` are in `MainWindow.Qevent` (`06000089:001B/0037`):
two sequential one-byte writes. Its caller invokes it synchronously before
starting the queued update worker. No concurrent in-process requests or uses
of the generic read/split branches are established by this call inventory.
External, reflected and unrecovered calls are outside that scope.

## Native port API boundary

Static inspection of the bundled x64 DLL narrows the native uncertainty. In
its driver-backed branch, `Out32` at RVA `0x1040` makes one synchronous
`DeviceIoControl` call at `0x108C`, with a two-byte port and one-byte value.
It discards the Boolean result and returns no outcome. `Inp32` at RVA
`0x10A0` similarly submits one port read at `0x1113`, using the same buffer
for the input port and output byte. Failure is logged, but execution still
returns that buffer's first byte at `0x1147`. The updater cannot distinguish
that failed-read return from an actual device byte through this API.

Neither reviewed export acquires a mailbox lock or begins/ends a multi-call
mailbox transaction. Lock-related imports elsewhere in the library do not establish
such protection. The driver-open path requests share mode zero in
`CreateFileA` calls at RVA `0x1CEB/0x1D59`; that request alone does not prove
the driver's create/open path enforces exclusive ownership. It also cannot
establish exclusion of independent firmware field writers.

Static analysis of the embedded x64 driver resolves that create/open question.
At driver RVA `0x15FF`, the code supplies `Exclusive = false` to
`IoCreateDevice` at `0x160C`. Its create dispatch, installed at
`0x162A..0x1631`, is routine `0x1010..0x1031`: set success and zero information,
complete the request, return success. That path has no owner state or share
check. Device-control dispatch is separately installed at `0x1635..0x163C`.
Its byte-read branch performs `IN` at `0x135A`; the byte-write branch performs
`OUT` at `0x1382`. Each goes to ordinary request completion at `0x14AC`, with
no ownership held across the index and data operations. These RVAs use the
driver's preferred image base `0x10000`, distinct from the DLL's base.

The driver was analyzed directly from the original DLL resource in memory as
data, without loading it into Windows. This is a bounded review of the create
and byte-I/O paths, not a full driver trust/security audit. These driver paths
do not enforce a sole owner or protect the index/data pair, and the DLL API
fails to preserve usable per-operation failure results. Live Windows access
policy, actual competing handles and installed driver binaries were not inspected.

## EC scheduling and live request fields

The interrupt-dispatch table word at `0x611C` points to `0x20A7E`, which
calls `0xBEE6`. That routine sets the single pending byte `0x8000D` to one
at `0xBEEC`. Main event dispatcher `0x2115E` clears a pending byte at
`0x2118A` before calling its table entry at `0x21192`. Slot 13, word
`0x2E688`, selects `0xCD64 -> 0x12D80 -> 0x203C`.

The pending byte is a flag, not a count or a copy of a request. Repeated event
notifications while it is already one do not create separate software queue
records. This does not prove how every hardware notification is delivered.

| Mapped handler state | Recovered sampling behavior |
|---|---|
| Command | Loaded once at `0x2042` for the mapped branch. |
| Bank/table | Bank checked at `0x22D6`; table pointer selected once at `0x22EC`. |
| Count | Initially bounded at `0x22DE..0x22E6`, then reread for every loop test at `0x2318` or `0x236C`. |
| Register index | Reread at `0x231E` or `0x2372` for each byte. |
| Write data | Read from the live mailbox at `0x238C` immediately before each mapped store. |

The index is narrowed to a byte at `0x2322/0x2376`; a stable two-byte request
starting at `FF` addresses `FF` then `00` in the selected bank. Zero count
passes validation and performs no mapped access. A changed count is not
revalidated against 32 in the loop. The initial bound is therefore conditional
on the fields remaining stable; it is not protection against a concurrent
replacement. No overwritten-field experiment was performed.

After the handler returns, `0x12D8C` clears command `93` unconditionally.
It does not check that the current command still belongs to the handled
request. Under a hypothetical competing submission, an old handler could
therefore clear a replacement command. A newly pending event could then find
command zero and do no work. This is a conditional instruction-backed example,
not a measured race or a claim that such timing is unavoidable.

## What completion does and does not mean

| Actual register observation | Supported interpretation for an isolated, stable mapped `A0/A1` request without reset |
|---|---|
| Status `80` while command remains nonzero | Not completion: `0x22F4` sets it before the mapped loop. |
| Submitted command changes to zero | The selected handler returned; the byte itself does not identify which request completed. |
| Command zero plus status `80` | Handler-level success, including zero-length requests and skipped read-only writes. |
| Matching mapped read | A separate value observation; it needs its own intact request and response. It does not prove fan rotation or continuing ownership. |

Mapped status `84` means bank above 7, `82` count above 32, and `83` an
unsupported command reaching this mapped-command fallback (`0x23A4..0x23B8`).
For `A1`, metadata value `1` skips the write at `0x2388` without replacing
status `80`; otherwise the handler stores the whole byte. Successful writable
stores also set event byte `0x8000E` at `0x2394`. That later event has its own
slot (`0x2E68C -> 0xCD6C -> 0x9008`); clearing command `93` does not wait for
that subsequent handler. No particular fan effect is inferred from this event.

Command zero is also a lifecycle value. Initialization clears it at `0x12D7A`,
the alternate startup branch at `0xBDA4` clears it without clearing status in
that routine, and bulk initialization `0x12D92` clears the entire mailbox
range. These are not proposed recovery commands. They show why a zero command
alone cannot distinguish ordinary completion from lifecycle interruption.

The updater further weakens completion handling:

- All branches read status only once at `06000051:02D2..02E6`. A split
  request sends its second command after the first command clears without
  examining the first status. Success of the second chunk can hide failure of
  the first; failure of the second does not undo the first.
- Reads copy mailbox data into shared `I2CBUF` before that final status check.
  A failed read may already have changed the output buffer.
- Unsupported address/operation or count above 64 goes directly to the
  existing status without submitting a command. Stale `80` can return integer
  `1` for a no-op. Valid zero count instead publishes an empty request.
- Polling has no deadline, cancellation or cleanup. Adding a host deadline
  would limit waiting, but would not cancel or establish the result of an
  already submitted EC request.
- `Qevent` discards both integer results (`06000089:0020/003C`) and catches
  exceptions without publishing failure. Its fixed sleeps are not polling
  timeouts. Its presence is vendor-use evidence, not validation of the generic
  helper's failure handling.

## Consequence for an adapter

The useful boundary is now clearer: this mailbox avoids the MCU shared
descriptor and has command-last publication followed by handler-return clear.
It still lacks an established owner and request/response correlation. A host
mutex can serialize cooperating callers; it cannot establish exclusion of
other port users, lifecycle resets or independent writers of the target fields.

Before a live adapter can use this path, evidence must establish a supported
access mechanism that protects the index/data pair and complete transaction,
identify the other actors it excludes, and distinguish completion from reset
or replacement. A typed implementation would also need immutable bounded
requests, per-transaction status validation and independent mapped readback.
It must stop after timeout or ambiguity rather than retry, clear the command,
or copy the updater's second-chunk behavior. There is no new live adapter or
port-access dependency in this change.

Even a solved mailbox contract would leave the separate requirements for
[direct-duty restoration and cooling recovery](so6120-direct-duty-safety.md#decision-and-remaining-evidence).
This audit does not establish those properties or justify a fan-stop switch.
