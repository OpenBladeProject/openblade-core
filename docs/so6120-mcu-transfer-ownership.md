# SO6120 MCU transfer ownership audit

The 2026-11-05 offline follow-up finds that the generic EC register interface
shares its transport with more than the thermal worker. The recovered command
readiness and initialization checks do not supply an exclusive access interval.
This narrows a prerequisite for a Silent direct-duty experiment; it does not
establish an observed collision or authorize a hardware probe.

The input is the same exact MCU 1.07 image pinned in the
[bridge analysis](so6120-mcu107-ec-fan-bridge.md#exact-inputs-and-method).
The [offline explorer](../tools/firmware/README.md) records static annotations;
its internal MCU addresses are not exposed host-readable EC indices. No vendor
code was executed and no hardware was accessed.

## Shared transport clients

An exhaustive scan of explicit direct branch/call operands in the existing
recovered application listing finds 30 calls to read helper `0x5864`, 24 to
write helper `0x58B0`, and two to transfer routine `0x2520`. Those two transfer
calls are inside the helpers. This inventory is scoped to the recovered listing;
indirect, computed and unrecovered call paths are not proven absent.

| Recovered caller | Read sites | Write sites | Evidence and role |
|---|---:|---:|---|
| `0x42FC` | 0 | 1 | Lifecycle path writes EC index `07` at `0x4316`; reached through main-loop routine `0x446C`. |
| `0x4AEC` | 1 | 1 | Thermal initialization reads index `00`, then writes `90`; reached from startup, reinitialization and the worker. |
| `0x4B5C` | 28 | 14 | Queued and periodic EC work, reached from main at `0x66E8`. |
| `0x59E0` | 0 | 2 | Peripheral initialization also uses single-byte wrapper `0x610C`. |
| `0x610C` | 0 | 1 | Generic single-byte write wrapper; it adds no ownership check. |
| `0x6580` | 0 | 2 | Peripheral lifecycle/configuration work; also uses `0x610C`. |
| `0x7164` | 1 | 1 | Generic EC command handler, reached from timer-dispatched `0x9598`. |
| `0xA8F8` | 0 | 2 | Periodic peripheral writes, reached from main at `0x66E4`, immediately before the thermal worker. |

The table used by peripheral writers is at flash `0xB102`, with address bytes
`40 5E 4A 54`. At `0xA95C/0xA98A`, periodic work writes 144-byte payloads
through the same helper, using a table-selected address or `40`. Peripheral
identity is not needed to establish that these are additional transport clients.
This is not evidence that each client is active in every runtime state.

Both helpers resolve their descriptor to `0x100000D4` and buffer to
`0x1000024C`. They overwrite transfer counts and address/index bytes before
calling `0x2520`, without first acquiring idle ownership. That routine resets
progress and sets shared status to busy (`06`) unconditionally. Consequently,
quiescing only pending thermal work would not prove the whole transport idle.

## Command readiness is separate from bus ownership

USB report handling `0x2460` marks a 90-byte command ready at `0x10000148`.
The recovered endpoint-zero callback calls that handler at `0x39F8`.
Dispatcher `0x9598` consumes the flag and clears it after processing; its only
explicit direct caller in the listing is timer code at `0x35E6`.
The ready flag indicates a command to process, not a drained bus transaction.

The recovered timer path checks `0x1000014D != 0`, initialization phase
`0x100001E9 >= 3`, and the condition `0x100001E8 == 3` or bit 3 clear in
the halfword at `0x1000014E` before reaching that dispatcher. Startup writes
phase 1 in `0x5E50`, phase 2 in `0x5E04`, and phase 3 in main at
`0x66CA..0x66CE`. None of these checks compares the transfer busy word,
the thermal pending mask, or a transaction owner/generation.

Targeted additional Thumb decoding resolves `0x1000014D` as the USB
configuration value. The standard request-8 branch returns it; request 9
reaches `0x3F4C`, which stores the descriptor's accepted configuration value
at `0x3F6C` or clears it at `0x3FB2`. Literal `0x3FCC` is `0x10000164`;
the code subtracts `0x18` and accesses byte `+1`. Reset helper `0x40B8`
also clears that byte and the `14E/14F` halfword. These are configuration and
lifecycle operations, not a transfer acquire/release. The selected switch
tables were treated as data rather than linear instruction paths. The bounded
producer audit below resolves concrete writes to application-state byte `1E8`;
complete external mode meanings and unrecovered writers remain unresolved.

Main calls the lifecycle routine, peripheral writer and thermal worker at
`0x66E0/0x66E4/0x66E8` before its later checks of those application/USB flags.
The worker's inactive-cache branch retries initialization, which itself uses
the bus; an inactive cache marker is not an established pause operation.
The separate generic-access gate `0x100000C1` does not change these workers.

## Application-state producers and restoration limits

The follow-up audit pins the same 57,764-byte MCU image, SHA-256
`90bbe0d8dd1ca31614a2465c7c8821347b2a667606c39bedadbd59b98886344f`.
It identifies three explicit byte stores to `0x100001E8` in the recovered
application listing, plus decoded startup initialization:

| Store site | Value and evidence |
|---|---|
| `0x4102` | Zero during USB reset/lifecycle handling. `0x40FE` loads literal `0x4114 = 0x100001E8`; `0x4100` supplies zero. USB handler `0x3AAE` calls `0x40F8`. |
| `0x5E78` | Zero during application initialization. Literal `0x6010` supplies the destination; main calls initialization at `0x66BE`. |
| `0x72C6` | Class `00/04` copies the full payload byte at report `+8`, before comparing it with `1`, `5` or `2`. The recovered handler does not constrain the byte to `0..5`. |
| `0x2212` | Startup scatter decoding zero-fills the byte. Record `0xE124` is `(source=0xE144, destination=0x100000C0, length=0x160, decoder=0x21E0)`. The decoded zero run of 84 bytes from control `0xE1A1` covers `0x100001E8`. |

An offline translation of decoder `0x21E0..0x2234` expands exactly 352 bytes
and consumes through image end `0xE1A4`. This transforms image data; it does
not execute firmware. A separate boot scatter record at `0x1CDC` clears
`0x10000040..0x100013A7` through routine `0x0120`, also covering this physical
address. Neither initialization path is a reversible application command.

The state setter has effects beyond the state byte:

| Requested value or transition | Additional recovered effect |
|---|---|
| `1` | `0x72FC` calls nonreturning reset routine `0x46B4`, which masks interrupts, writes `0x05FA0004` to `0xE000ED0C`, then loops. |
| `2` | Clears generic-access gate `0x100000C1`, stores the secondary payload byte in `0x10000204`, and, if that byte is zero, clears `0x10000124/125`. |
| `5` | Sets generic-access gate `0x100000C1` to one. |
| `0`, `3`, `4`, `6..255` | Clears the generic-access gate; applicable transition effects below also occur. No special setter branch for `4` or `6..255` was recovered. |
| Old `3` to `0` | Clears `0x10001A4A` at `0x72B6`, then calls `0x9BB8(1)`. |
| Old value other than `3` to `3` | Clears `0x10001A4A` at `0x72C2`. |

These are static input-domain and side-effect findings, not admitted values
to probe. Class `00/84` returns the state sampled at `0x71A2..0x71A6` through
`0x729A/0x733A`; it supplies no bus-idle, drain or generation acknowledgement.
The setter saves none of the original auxiliary values. Replaying the old
state byte therefore does not establish exact restoration; it cannot recover
cleared auxiliary fields, and value `1` does not return. The gate also has
independent writers, so its original value cannot be inferred from this byte.

Coverage is deliberately bounded. The scan found 21 exact target-literal words
and followed affine aliases from 1,146 RAM-literal loads in the recovered
listing. Three seeds (`0x0B3C`, `0x2522`, `0x7166`) reached its traversal cap.
The scanner does not prove absence of computed, interprocedural, bulk or
unrecovered writes; the startup decoder and boot clearing routine were reviewed
separately. Inline switch tables were treated as data. This is not a complete
enumeration of every possible state producer.

## Parking entry and generic dispatch have incompatible gates

The relevant halfword at `0x1000014E` is USB lifecycle state: handler
`0x4214` sets bit 3 at `0x4222`, and the alternative USB interrupt branch
clears it at `0x3AD6`. Its complete external meaning is not needed here.

Lifecycle routine `0x446C` initially enters its low-power loop only with bit 3 set
(`0x446E..0x4474`), USB configuration nonzero, application state other than
`3` (`0x449C..0x44A0`) and other than `2` (`0x44AA..0x44AC`), plus additional
wake conditions. It calls preparation `0x42FC` at `0x44C8`, then low-power
routine `0x2984` at `0x44D2`; that routine contains `WFI` at `0x29EA`.
Preparation already performs an EC write at `0x4316`. Clearing bit 3 makes
the parked loop set a wake event at `0x44E2..0x44EC`; resume calls `0x4120`
at `0x455C`, which includes peripheral and thermal reinitialization.

The necessary gates for parking entry and timer generic dispatch are:

| Path | Necessary predicate, omitting additional configuration/wake gates |
|---|---|
| Main-loop parking entry | `USB bit 3 set && state != 3 && state != 2` |
| Timer dispatch (`0x35CE..0x35E6`) | `state == 3 || USB bit 3 clear` |

All 512 combinations of the full state byte and this status bit make these
predicates mutually exclusive. This is a static check of necessary predicates,
not execution, emulation or a claim that asynchronous transitions cannot occur.
There is no shared state snapshot that admits both initial parking entry and
generic dispatch. Selecting `3` bypasses the dispatch restriction but excludes
that entry; clearing the status bit admits dispatch while also initiating wake.
The local retry at `0x4550..0x4556` can return to `0x44D2` without resampling
the application byte: these are entry checks, not a continuously enforced
parked-state invariant. Neither the checks nor a possible asynchronous state
change supplies a drained, acknowledged ownership lease.

Outside that parking path, the main loop calls peripheral work at `0x66E4`
and thermal work at `0x66E8` before its later state checks. The peripheral
transfers at `0xA95C/0xA98A` precede or bypass its state test. Thermal entry
`0x4B5E` uses active marker `0x10000220`, not `0x100001E8`; its queued write
at `0x4B98` needs no application-state check, while inactive entry calls
bus-using initialization. These paths do not establish a worker pause for any
new state value. No collision or physical fan behavior was observed.

## Remaining interference and confirmation boundary

The [priority and watchdog review](so6120-direct-duty-safety.md#mcu-ownership-completion-and-recovery)
establishes source-configured interrupt priorities and a transport wait that
re-enables interrupts while feeding the watchdog. No helper-level lock protects
its shared setup or completion word.

A concrete conditional interleaving remains: normal work prepares or waits on
a transfer; a ready generic command reaches the timer dispatcher; its helper
replaces the same setup/progress/status; normal work later resumes without a
transaction identity to distinguish the two results. The inspected code does
not establish exclusion of that interval. This is a static risk scenario, not
a hardware observation or proof that every request collides. Even a generic
read uses the shared mutable transport state.

Outer report success, embedded bus completion, actual mapped values and physical
fan rotation remain distinct. The corrected manual-write/readback bit meanings
and the restoration/recovery requirements in the
[readiness notes](so6120-ec-register-semantics.md#offline-inspector-follow-up-and-test-readiness)
still apply. Host-side request serialization alone does not serialize firmware
workers against timer dispatch.

The next useful evidence is a firmware-defined ownership/drain mechanism or an
independently established serialized access path covering every relevant client.
None is established by these gates. Until then, constructing a generic read-only
collector would not by itself make its live use justified. Thermal recovery and
exact restoration also remain necessary before a direct-duty experiment.

The subsequent [host mailbox and pause-command review](so6120-host-mailbox-alternatives.md)
finds a vendor host-port mailbox path that bypasses the MCU descriptor, while
retaining unresolved mailbox ownership and thermal recovery. It also resolves
the concrete application-state-5 branch: it enables generic access without
excluding the normal bus clients. Neither finding admits a live test.
