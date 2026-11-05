# EC 1.04 fan PWM writers and direct-mode recovery

Static analysis of `block-07-044de8.bin` (SHA-256
`8537e9fd55b7b35504d8d87b0191acc4391b67365b3fe72f6e1029a424c9e707`),
`ec-disassembly.txt`, `ec-decompiled.c`, and the decoded bank map. The census
matches fixed-offset stores to their MMIO base and checks the named decompiled
globals, raw image pointers, and relevant address-table consumers. Listing
coverage and unresolved computed targets bound the result.

## Fixed-address stores

There are 24 recovered direct byte stores to the two actual fan duty registers.

| Function or branch | Stores to `F01807` | Stores to `F01803` | Conditions |
|---|---|---|---|
| `15510` normal tach/target feedback | `15566`, `155AC`, `155C0`, `155F6`, `15606`, `15684`, `1568C`, `156B8` | `15712`, `15758`, `1576C`, `157A2`, `157B2`, `15830`, `15838`, `15864` | `C834` calls periodic `1586A`. Active bit `802E3.0` and cadence gate the controller. `158AE..158B6` skips this writer for `FE`. |
| `1488A` inactive reset | `148C0` | `148C4` | `15876` takes the `158CA` call when `802E3.0` is clear. `148A2` sets the selector from the configured fallback getter and the PWM writes clear duty. |
| `14948` state reset | `14980` | `14984` | `DA00` calls at `DAD4`. `1495C` invokes `D08C -> CFE0` to replace the selector with the configured fallback. Both PWM writes clear duty. The recovered `1D4D8 -> DC08 -> DA00` path depends on an EC input event and its sampled state, not a process timer. |
| `15E46` selector `FF` | `16210` | `1623E` | Requires active state and `80425.6`. A changed target selects the corresponding duty from the table at `29ACC`. Separate selector from `FE`. |
| `15E46` selector `FE` | `1628A` | `162BA` | Requires active state and `80425.6`. Copies `80394/80395` on requested/actual mismatch at `1627C` or `162AC`. The examined branch has no temperature, lease, or host-liveness check. |

The other direct-looking `0x807` stores at `B12C/B1CE` use the `87000` RAM
base. Those stores are outside the two MMIO registers. No fixed-offset wider
store overlapping the two duty bytes appeared in the checked `F01000`-base
listing. The fixed-address census omits unresolved data-derived destinations.

`15E46` runs from `CAB4` via the `801C0` pending path at `2100C` and from
the conditional `C70C` call. EC tick code `6774..678E` increments `801C0` on
its own phase. If an outside writer changed a duty byte while the active `FE`
selector and permission persisted, the next eligible `15E46` evaluation
could restore the staged request.

## Address-table and command-supplied targets

- The raw PWM table at `309FC` uses 12-byte records. Channel 1 (`30A08`)
  resolves field 0 to `F01803`, and channel 5 (`30A38`) resolves it to
  `F01807`. Generic clear helper `27E46` dereferences that field and stores
  zero at `27E5A`. Its recovered direct callers at `C18E/C194/C19A` pass
  channels 0/2/3. Fan setup `14758` calls `27CF4` with 1 and 5, but that
  helper uses field `+4` for clock selection and the separate group table
  at `30A5C`. Its `27D78/27DB6/27DC8/27DD2/27DD8` writes set group and clock
  parameters, including a shared cycle register, rather than the duty field.
- Raw 32-bit references to the two duty addresses also appear at
  `29BF8/29C20`, field `+0x0C` of the two fan metadata records starting
  at `29BEC`. The recovered `29BEC` consumers at `12A4C`, `12AC8`, and
  `12B16` use fields `+0x18`, `+0x14`, and `+0` respectively. The store at
  `12A64` targets the `+0x18` records `802C4/802C6` for those two channels.
- Bank 0 `B0/B1` entries at `2DE58/2DE60` carry flag 1. The A0/A1 mapped
  dispatcher treats those PWM aliases as read-only. Two separate command
  branches use supplied addresses without that bank table. `D1` in `203C`
  has a size ceiling of 32 bytes and copies to a supplied address at `210C`.
  The recovered EC event path is `BEE6 -> 8000D -> CD64 -> 12D80 -> 203C`.
  Legacy command `91` branches `12F26 -> 13464` and requires `80450 == 87`
  before `12DF4` reaches its computed-address byte store at `12E14`.
  The store instructions can resolve to either PWM address. These are
  command-driven paths, with no recovered automatic trigger or durable
  override of `FE`. Static reachability within EC firmware does not establish
  a physically available host path or authorize exercising either command.

## Watchdog and lifecycle boundaries

EC timer callback `667C` drives `801A0`. The periodic phase at `675A..6772`
increments pending `801BC`, which the EC dispatcher consumes at
`20FEC..20FF8`. The resulting `C8CC` work writes `5C` to watchdog register
`F01F07` at `C8E0` before its later state checks. `24868` programs the
watchdog register block and also feeds it at `2491A`. The recovered feed
schedule depends on EC execution rather than traffic from a host process.

The listing includes deliberate watchdog-style reset paths. `DA00` reaches
one at `DBE6..DC04` under its `80145 == A5` branch. Another timer-dispatched
routine reaches `16A22` via `21098 -> CD14 -> CD2A`. It requires
`80901 == 0`, an expired `80917` counter, `80916 != 1`, and a nonzero
`169E4` result before marking and writing persistent state through `16992`.
It then programs the watchdog and spins at `16A64..16A6E`. The comparison
routine `169E4` uses persistent record signature, marker and bytes
`87F00..04`, `87F0F`, `87F10..19`, and `80200..04`. No fan-mode or host-loss
predicate is visible in this reset path. On the ordinary initialization path,
`3066 -> B8BA -> 1247C` selects a normal mode at `124AA/124C2` before fan
setup. This is evidence for a different lifecycle, with no proof of bounded
watchdog expiry, exact previous-state restoration, or fan output across reset.

Separate state transitions can replace `FE` without an emergency-duty store.
For example, `A5C6/A5E4 -> 124D0` calls the configured selector getters when
`808F4 != 2`. The state sequence `196C2 -> 192DA -> CFE0` also rewrites the
selector from those getters. It comes from changes in the state contributors
compared by `18E6C` and the later state-machine gates in `195D4`. Neither
path establishes an automatic handback on host-process loss. Clearing
`80425` through `12D08` also permits the `FE` branch's fallback selector
write at `162DC`, but the known clear calls have separate lifecycle gates.

The writer census found no independent high-duty writer with a reachable
active-`FE` trigger or a bounded host-loss trigger that clears `FE` or its
permission. That leaves host-loss recovery unresolved even though the EC can
change state for other reasons. Indirect or unrecovered firmware, hardware
protection outside these registers, actual timing, and physical fan behavior
remain outside this evidence.
