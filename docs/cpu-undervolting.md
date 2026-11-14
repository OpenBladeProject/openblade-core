# CPU undervolting

CPU Voltage Optimizer adjusts the running CPU's all-core Curve Optimizer offset.
Values are integer Curve Optimizer steps, not millivolts. On remembers a negative
offset from -30 to -1. Off requests zero and keeps that editor value.

| Laptop | CPU | Access | Evidence |
| --- | --- | --- | --- |
| Blade 14 (2024), RZ09-0508 (`1532:02B6`) | Ryzen 9 8945HS | Live control through the already running reviewed AMD V22 stack | Exact-model source review; physical validation pending |
| Blade 14 (2023), RZ09-0482 (`1532:029D`) | Ryzen 9 7940HS | Live control through its reviewed AMD V22 stack | Exact-model source review; physical validation pending |
| Blade 16 (2025), RZ09-0528 (`1532:02C6`) | Ryzen AI 9 HX 370 | Existing AMD V31 live backend | Existing native readback and recovery evidence; see [model details](rz09-0528-cpu-tuning.md) |
| Blade 14 (2025), RZ09-0530 (`1532:02C5`) | Ryzen AI 9 365 | Feature discovered; control unavailable | Its ten-core native contract is not admitted |

Connect a full-power AC adapter and close competing tuning software.
Open CPU Voltage Optimizer and select **Refresh**. OpenBlade checks the exact
laptop and already installed, running AMD backend, then reads every enabled core.
After a complete read, choose an offset and select **Apply now**. Mixed readings
require an explicit editor choice. **Restore** returns every core to its exact
original value while the recorded ownership and hardware context remain valid.

Availability checks and Refresh do not install, start or remove drivers. No
Razer or AMD binaries are bundled. The V22 integration requires the reviewed
AMD SDK 2.12 package already used by these exact Synapse products. Unsupported
or changed raw driver contracts remain unavailable.

The Blade 14 backend currently admits PM table `004C0008`, length `AF0`. This is
a native data-layout check rather than a BIOS version pin. Other layouts remain
unavailable until their complete interpretation is reviewed. Firmware shadows
and successful setter acknowledgments do not confirm a running offset.

## Transactions and compatibility

The shared planner preserves a complete signed per-core baseline. Before each
possible submission, it checks fresh exact identity, CPU/topology, controller
absence, session, full power, installed-driver continuity and the firmware's
actual offset range. It journals intent before the native setter and requires
independent complete curve readback plus unchanged Fast, Slow and STAPM power
peers. Ambiguous submission or failed cleanup blocks further writes without
replay or guessed restoration.

Phoenix/Hawk Point uses its own ordinal-based `E1` getter, `53` per-core setter,
PCI identities and eight-core fuse interpretation. It checks the source-defined
capability and fresh `5000A` magnitude range. It does not inherit the Strix
backend's fused PPT ceiling or expose a power-limit setter.

New model ownership uses journal schema 4 with an explicit closed contract ID
and version. Existing schema 3 journals retain their original RZ09-0528/V31
meaning, backend pins, originals and recovery rules. A new laptop or backend
cannot adopt their restoration authority. The 2025 Blade 16 uses the shared
planner and recovery code while keeping its reviewed native access path. The
Razer wrapper is unsuitable as a replacement because initialization can manage
drivers and its getter collapses a full core vector to one value.

New next-boot requests remain retired. Legacy saved observations and exact-original
journal recovery remain compatible. Intel voltage offsets use a different API and
units and are not inferred from these AMD contracts.

The [pinned source review](../evidence/source-reviewed-blades/2026-11-14-cpu-undervolting.md)
records package hashes, native method locations and remaining physical tests.
Offline tests cover both Blade 14 models, malformed replies, complete mixed
restoration, cancellation, uncertain submission, ownership and old-journal recovery.
No new-model on-device voltage, driver installation, reboot or sleep test was
performed for this implementation.
