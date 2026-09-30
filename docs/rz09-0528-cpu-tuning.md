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

A later attended test of the actual service transaction stopped before submitting
a command. A read-only diagnostic found that the COM getter returned output but
omitted the `ReturnValue` property exposed by the CIM helper. The decoder now
distinguishes confirmed absence from property-read errors for this exact provider;
version 7 and command payload checks remain mandatory. A subsequent attended
getter passed with exactly one output property: UInt32 version 7. All eight full
saved-pair checks matched the original baseline. The subsequent attended cycle
using the actual service classes saved zero, verified both copies, and restored
the original −1 value through journal reconciliation in a new process. Both
800-byte copies restored exactly and the journal reached `Restored`. This test
did not exercise installed-service IPC or establish boot activation.

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

The developer PM observation command is connected to a native supervisor,
parent authority listener, and pinned evidence writer. It requests three idle
samples with fresh identity, power, session and driver-lifetime checks. The
reviewed launcher pins the complete payload and binds the actual parent and
child processes. Cleanup requires confirmed child termination and authority
revocation before the supervisor removes its own driver registration. Unknown
completion retains the original processes and evidence without retrying.

An attended run on 2026-09-30 retained three complete private tables for version
`0x005D000B`. Independent review verified reply and payload bytes, file hashes,
provenance, timing, and the complete driver lifecycle journal. Separate checks
confirmed process termination and temporary driver-registration absence. This
validates the capture path; table fields do not yet supply an admitted power-limit
value or restoration baseline. Production PM invocation remains blocked.

The vendor parser and command implementation, corroborated by primary upstream
source ([UXTU](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility/blob/eeb43042883bc9a80847999b2957b6ded86938a9/Universal%20x86%20Tuning%20Utility/Scripts/AMD%20Backend/RyzenSmu.cs#L224)
and [RyzenAdj](https://github.com/FlyGoat/RyzenAdj/blob/5775fc3e6dbb25c7030ee2d100a1bdd6e8bf2d0a/lib/api.c#L1470)),
map command `0x32` to the fast-limit field and `0x33` to the slow-limit
field. The captured candidates correspond to 85 W and 80 W. Restoration must
preserve each independently. The vendor's fused value comes from separate SMN
registers and was not part of the initial capture. The full writable range,
physical setter readback and independent restoration remain unproved.

A subsequent attended query read both source-mapped registers and retained three
complete PM tables. Independent review verified all 18 exported files and the
complete driver lifecycle. The registers decode to 71 W, while the tables still
report 85 W fast and 80 W slow limits. The fused value therefore does not establish
an unconditional live power ceiling. Its relationship to the vendor capability
flag and firmware range needs further validation before power-limit writes.
Each new run uses its own independently pinned preset before launch, preserving
earlier protected payloads and evidence.

A separate developer range-observation profile reads the capability response,
four fixed source-mapped SMN registers, one PM table and the continuous min/max
firmware getter. The exact firmware describes PPT as a continuous-range entry;
its discrete-value getter returns an empty packet for this selector. The command
must match a separately reviewed run preset and
explicit attended approval. Metadata errors retain the completed PM sample and
copied fields as incomplete evidence. The existing three-sample profile cannot
invoke these additional queries.

The first attended range run retained the four register reads, capability
response and one PM table, then stopped at the empty discrete-value response.
Independent review verified the exported bytes, timing and complete owned driver
cleanup. The firmware trace explains that response, and the capture now selects
the continuous getter directly. The corrected metadata path still needs attended
validation; these observations do not admit power-limit writes or restoration.

Admitted live power-limit readback and independent restoration remain
unresolved. A curve-only observation therefore reports unknown power limits.
The unwired live curve transaction now permits repeated edits after a fresh
complete observation matches its owned state. Its bounded journal keeps the
original 12-core baseline separately from the current operation. Restoration
covers every remaining owned change, including changes from earlier edits, and
each reverse command requires independent admission. Ambiguous submission,
drift, or malformed recovery data blocks further edits. Earlier journal schemas
remain blocked in place. Live recovery represents the loader sequence with an
explicit source and nullable unsigned counter, rather than a boot GUID. Zero is
a valid counter. A reviewed normal-token diagnostic observed short-term equality
between separate processes; reboot and power-transition continuity remain unproved.
Fake-backend tests cover these paths; native mutation
and a trustworthy boot-continuity source remain unadmitted.
An internal CO transport reuses the query session for full before/after vectors,
checks fresh capability and range evidence, and records possible submission before
the driver call. Forward edits also require the exact immediate reverse to fit
that range. Uncertain cleanup blocks later operations on the retained platform.
Only fake tests supply its write proof; no production factory or provider exists.
Boot activation, installed-service IPC validation, and attended live
readback and restoration remain necessary before enabling the corresponding
controls.

The developer-only `--cpu-tuning-layout-smoke` harness renders seven synthetic
CPU states at two card widths. It checks saved/live separation, unknown live PPT,
pending boot changes, editing and restoration availability, and control bounds.
It writes screenshots to the private validation archive without connecting to
the service or sending CPU commands. These fixtures do not establish hardware
admission or validate the installed application.

Tracked in [issue #622](https://github.com/OpenBladeProject/openblade-core/issues/622)
and [draft PR #623](https://github.com/OpenBladeProject/openblade-core/pull/623).
