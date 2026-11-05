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
tables were treated as data rather than linear instruction paths. The complete
meaning and producer coverage of application-state byte `1E8` remain unresolved.

Main calls the lifecycle routine, peripheral writer and thermal worker at
`0x66E0/0x66E4/0x66E8` before its later checks of those application/USB flags.
The worker's inactive-cache branch retries initialization, which itself uses
the bus; an inactive cache marker is not an established pause operation.
The separate generic-access gate `0x100000C1` does not change these workers.

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
