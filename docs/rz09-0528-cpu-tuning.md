# RZ09-0528 CPU tuning

CPU tuning has two targets: settings saved for the next boot, and settings applied
to the running CPU. The CPU card keeps their observations separate. Refresh reads
saved firmware; opting into the installed AMD backend permits passive dependency
inspection. It does not start a driver or grant permission to tune the CPU.

This implementation is in progress. The service blocks tuning and restoration
requests until their backend, readback, and recovery paths are admitted. The
editor represents the observed 45–95 W power range and −30 to −1 curve optimizer
range. Disabling the optimizer means zero magnitude and retains the editor's
negative value. Each feature can be left unmanaged.

## Saved firmware

The exact BIOS 2.02 reader compares both saved firmware copies, their attributes,
and their complete 800-byte layouts. The service uses a temporary impersonation
token for the firmware-read privilege. Unsupported curve modes remain unknown.
Saved configuration is not evidence of the running CPU state or boot activation.

Attended validation on 2026-09-29 used the observed version-7 firmware interface
to save all-core curve optimizer zero and restore its prior −1 value. Both
submissions were acknowledged. Both saved copies changed only the magnitude byte
and then restored byte-for-byte. Power-limit fields remained unchanged. No reboot
or live CPU measurement was part of that test.

## Optional installed AMD backend

The optional backend is scoped to the RZ09-0528's exact signed AMD stack. Passive
inspection checks paths, signatures, hashes, and service state. It never installs,
enables, starts, stops, or removes a driver.

An internal attended-query adapter implements PCI discovery, topology checks,
PM-version queries, and per-core curve optimizer queries. These queries use PCI
transport writes and require a separate, single-use attended approval. Production
invocation remains blocked. Fake-transport tests do not establish hardware
admission, and hashes of installed files do not attest the image already loaded
in the kernel.

Live power-limit readback and restoration of both independent limit values remain
unresolved. A curve-only observation therefore reports unknown power limits.
Boot activation, durable restoration ownership, and attended live readback and
restoration remain necessary before enabling the corresponding controls.

Tracked in [issue #622](https://github.com/OpenBladeProject/openblade-core/issues/622)
and [draft PR #623](https://github.com/OpenBladeProject/openblade-core/pull/623).
