# RZ09-0528 CPU Voltage Optimizer

The optimizer changes the running CPU's all-core Curve Optimizer offset on the
Razer Blade 16 RZ09-0528 (`1532:02C6`). On uses the remembered integer from
−30 to −1; Off requests zero and preserves that negative editor value. These
are Curve Optimizer steps, not a voltage reading in millivolts.

The editor is live-only. It shows independently measured running offsets and
sends voltage-only requests. Mixed per-core readings require an explicit
choice before Apply. The optional backend must already be installed and
running; availability checks and reads do not provision or start its driver.

New saved-setting requests are retired. Existing serialized target values,
firmware observations and saved journals remain readable for compatibility.
Exact-original recovery of an existing saved journal retains its original
admission and complete paired readback. That recovery does not admit a new
saved edit or a live restore.

## Live command contract

The reviewed backend binds its signed AMD stack, retained files and device
handle to the exact host, USB identity, CPU topology and native ABI. The enabled
physical-core mask is `0xFF55` (twelve cores), PM version `0x005D000B` and PM
table length `0xD50`. The per-core setter is `0x53`; independent measured-offset
readback uses `0x5E`. Capability `0x82` and power-peer observations have separate
admission. A CPU-setting read can require PCI/SMN transport writes.

Every native step retains fresh model/device, topology, controller absence,
active-session, full-power, continuity, range and ownership proof. Query and
mutation workers remain serialized until actual native termination and
cleanup, including after caller cancellation. Failed proof revokes reuse.

The firmware range reader accepts interface version 7 and selector `5000A`.
It requires the complete typed Current/Min/Max/Stepping/Value response. Reviewed
metadata magnitudes are 0–50, step 1; the product exposes the narrower negative
range above. Current is a firmware shadow, never proof of running offsets.
Missing ReturnValue compatibility requires the reviewed fully enumerated
absence case; false, malformed and stale evidence deny.

Live apply and exact restoration require complete originals and unchanged
read-only Fast/Slow/STAPM power peers. Each possible submission is journaled
before the native call. Immediate native readback is consumed once, only when
bound to that exact context, command, purpose, original vector and power peer.
Missing evidence follows independent observation; malformed offered evidence
denies. Ambiguous submission, drift or uncertain cleanup blocks further
mutation without replay or guessed restoration.

The validation profile permits only −1 and zero, at most twelve forward and
twelve reverse setters and one nonrenewable 600-second budget. Normal builds
retain the reviewed −30 to −1 editor range. Live PPT setters remain absent;
historical PPT journals can block voltage writes but grant no power setter.
The installed AMD integration pins the reviewed reverse-engineered raw driver
contract. Broader installed-driver compatibility needs separately reviewed
package/catalog trust and compatible native ABI proof before those pins change.

## BIOS, EC and Synapse provenance

The 2026-11-07 review acquired exact-model
[BIOS 2.02](https://mysupport.razer.com/app/answers/detail/a_id/15235) and
[EC 1.09](https://mysupport.razer.com/app/answers/detail/a_id/21226) packages
from Razer. Both carriers passed Windows Authenticode as Razer USA Ltd. They
were extracted as data; neither carrier nor any extracted updater was run.
These versions identify reviewed sources, not a production BIOS allowlist.

| Source | SHA-256 |
|---|---|
| [SO6110_BIOSv0202.exe](https://dl.razerzone.com/drivers/RazerBlade16RZ09-0528/SO6110_BIOSv0202.exe) | `4E39E3F7BC9E7C09BD60D04ADE0AA38F63A7D23FD497934FFAA25C4FEA7E592F` |
| SO6110_202.rom | `5BBD09CDA602E8555B04D7CA2A3A7BCBBDFC929BE8A7BC5C81E6C3DFC7CBB2F4` |
| AodSmm body, GUID B54299AD-B521-4BB8-A361-689249FE72B9 | `2DE96B17BBF16650A7DFD194B5E15A08CBE3C6A9790133A66FDF284A3E14F857` |
| AodDxe body, GUID 442BA91E-B0A8-499F-94F7-2E922C9AAE0D | `DB6EACA19A6B5BDB1781642F88C6E5A5DB45E54BCBD9388552435CCEC6D08158` |
| [SO6110-ECv0109.exe](https://dl.razerzone.com/drivers/RazerBlade16RZ09-0528/SO6110-ECv0109.exe) | `3BF4DBEC9555DE4F784E9602B2FE982EC439924B946C412F46DB4C53A769832B` |
| SO6110_EC_0109.bin | `44DC4E4C92EA4BE9DEF93670A95B2BF859CB648B8DFE393FB5877735DEBBEF5D` |
| Synapse product 710 main.js | `201541BA7E521BB4B161CFCD077BC95EA2577BFFC37B3452D1CCBFB57A57F626` |
| Synapse product 710 policy.js | `9D0E95894BE265652412EA84B1B52713A825D083BA0B65C6D9B91C70F659691C` |

AodSmm callback RVA `0x4A78` dispatches saved selector `0x5000A` at `0x5233`.
Its all-core fields match the retained legacy encoder `0x11FF0000 | magnitude`,
including zero magnitude with mode 1. Shared completion at `0x52E2–0x530A`
changes the power-mode byte when all five adjacent power words are zero. These
findings support the legacy full-image simulator and peer preservation. They
do not revive saved Apply or establish stability at arbitrary offsets.

AodDxe RVA `0x6470` writes the named variable and its `%sLast` mirror with
attributes 7 and the same buffer, but discards the mirror-write status.
Acknowledgement cannot replace independent comparison of both complete
800-byte copies during legacy recovery.

Exact Synapse source is [product 710](https://apps.razer.com/synapse/products/710/mw/index.html),
build `2609300314`, revision `5d7f793ac59d077baa74a1e05c93c177f4f96701`.
Product 709 is a different Blade model. At zero-based character offset 977000
in the pinned policy, GetCurveOptimizerValue changes a returned zero to −1 and
calls its setter before updating presentation. OpenBlade uses independent
native per-core readback instead of treating that callback as passive measurement.

EC optimizer participation and a complete boot-to-SMU activation chain were
not established. Neither becomes a new prerequisite or command. The live
command and metadata contracts reuse reviewed exact-model source and capture
evidence; they were not executed again in this source review.

## Validation and remaining acceptance

Historical evidence includes saved Off/restored −1 across restarts, an older
complete live twelve-core −1 → 0 → −1 round trip and later separately recorded
failures and read-only reconciliation. The revised command-validation build
recorded another twelve-core transition and exact original restoration, but
its owner reported CompletionOrCleanupUnknown before authoritative completion
and cleanup receipts. That obligation remains unverified. Historical PPT
restoration is separate. Source review and simulated tests cannot settle either.

Reuse full-vector and unchanged-peer evidence where conclusive. Another
hardware operation requires a concrete remaining gap and applicable authority.
This source work ran on an RZ09-0581 and performed no CPU getter, setter,
recovery, installation, reboot or sleep test.

Coordinated source uses service IPC 73 with read-only compatibility 51. Shared
contract changes require a coordinated Service/Session Agent/Tray installation.
The [developer tools](cpu-validation-tools.md) and
[enrolled broker](unattended-deployment-broker.md) keep explicit job authority
separate from installation; deployment does not dispatch a CPU job automatically.

Tracking: [#622](https://github.com/OpenBladeProject/openblade-core/issues/622),
product [#623](https://github.com/OpenBladeProject/openblade-core/pull/623),
validation [#699](https://github.com/OpenBladeProject/openblade-core/pull/699),
deployment [#700](https://github.com/OpenBladeProject/openblade-core/pull/700),
and capture [#35](https://github.com/OpenBladeProject/openblade-captures/pull/35)
and [#36](https://github.com/OpenBladeProject/openblade-captures/pull/36).
Preserved original source: `archive/cpu-tuning-full-34ff4948`.
