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

The service transaction retains both complete original copies in a bounded
machine-level recovery journal. Repeated edits preserve that original baseline.
Before each provider call it records possible submission durably; only an
acknowledged command and exact full readback establish ownership. Timeout,
unexpected changes, or a failed mirror check block further commands. Explicit
restoration proceeds in reverse order and verifies every byte. These paths are
covered by fake-backend tests; production command admission remains disabled.

Recovery authority identifies its saved or live target and the features that
need restoration. A saved curve-only change does not require admission for an
unmanaged power limit, and saved recovery cannot enable a live restore. Missing,
malformed, drifted or uncertain recovery data blocks restoration. Every required
reverse command is admitted before the first restoration submission.

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

The developer `cpu-live-query-rz09-0528` command requires separate approval for
PCI transport writes and acknowledgement of a freshly loaded pinned driver.
It passively verifies a healthy exact USB devnode and installed-backend readiness
before the native adapter runs. It contains no driver lifecycle commands. On
2026-09-29, a fresh readiness check found the signed driver file present but its
service registration absent, so a separate attended provisioning proposal is
required before a live query.

Attended validation on 2026-09-29 measured all 12 enabled physical cores at curve
optimizer −1. The PM version was `0x005D000B`, with table length `0xD50`. The query
made no CPU-setting writes. Its private helper temporarily registered and started
the pinned installed driver, then stopped and removed its own registration after
the query exited. Independent checks confirmed registration absence and that both
validation processes had exited.

The validated adapter confirms the CPU node, SMN root bridge, and topology
function separately before its SMN transport writes. Earlier attempts identified
a developer payload dependency omission and an incorrect topology assumption;
both were corrected before the successful measurement. This observation does
not admit production live tuning or prove boot activation.

Live power-limit readback and restoration of both independent limit values remain
unresolved. A curve-only observation therefore reports unknown power limits.
Boot activation, validation of the service recovery workflow, and attended live
readback and restoration remain necessary before enabling the corresponding
controls.

Tracked in [issue #622](https://github.com/OpenBladeProject/openblade-core/issues/622)
and [draft PR #623](https://github.com/OpenBladeProject/openblade-core/pull/623).
