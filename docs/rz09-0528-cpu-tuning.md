# RZ09-0528 CPU tuning

CPU tuning has two targets: settings saved for the next boot, and settings applied
to the running CPU. The CPU card keeps their observations separate. Refresh reads
saved firmware. Opting into the installed AMD backend also permits fresh live CO
and PPT queries when the exact signed stack is already running and runtime admission
passes. It never starts or provisions a driver.

The draft service admits saved next-boot commands for the exact RZ09-0528/SKU
when fresh safety checks, paired readback and a reversible original
plan pass. Attended installed-service IPC tests passed CO disabled-zero and PPT
45 W, each followed by exact original restoration. The draft source now wires
optional live CO and read-only PPT through a shared retained device lifetime and fresh metadata proofs;
this integration has not yet been installed or physically validated. Live PPT
mutation remains unavailable in default composition.
The editor represents the 45–95 W power range and
−30 to −1 curve optimizer range. Disabling the optimizer means zero magnitude
and retains the editor's negative value. Each feature can be left unmanaged.

Admission is model/SKU scoped. BIOS 2.02 identifies the attended captures below;
BIOS values, including missing or inconsistent provenance, do not grant or revoke
CPU admission or ownership. Firmware interface version, complete layout, exact
hardware, backend lifetime and all command-specific safety checks still apply.

## Saved firmware

The exact-model reader compares both saved firmware copies, their attributes,
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
covered by fake-backend tests. Saved source admission independently checks the
eight-byte command encodings, every transition byte and both original reverse
paths. Each owned edit also validates its immediate inverse separately from the
plan restoring the first original pair. PPT zero reset is available only for
exact original-default restoration. The reviewed semantic candidate is in
[capture PR #35](https://github.com/OpenBladeProject/openblade-captures/pull/35).
Physical tests covered CO zero/original −1 and PPT 45 W/default; they did not
exercise every selectable value or the installed application. Later installed-service
validation is described below.

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

An attended test of the published Service classes saved a 45 W PPT limit and
verified both complete firmware copies. A new helper process recovered the real
ownership journal and restored the original default PPT configuration with the
exact zero-reset command. Both 800-byte copies matched their independent
originals, CO remained -1, and the journal reached `Restored` with no pending
steps. No reboot, live-setting command or installed-application change occurred.
This verifies the exact saved transaction and recovery path; boot activation and
installed IPC were unverified at that stage.

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
the continuous getter directly. The published continuous metadata reader subsequently passed attended
validation, described below. These observations do not admit power-limit writes
or restoration.

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
An internal service reader acquires that named counter with one fixed four-byte
read of its own process mapping on native AMD64 and the reviewed Windows build
10.0.26200 with service-pack major/minor zero. Its version gate uses the numeric
fields returned by `RtlGetVersion`, independently of the app compatibility
manifest. Failed platform/version checks, failed or incomplete copies and
cancellation return unknown without retaining a previous value. It supplies no
CPU-state or driver-lifetime continuity and is not wired into production admission.
Fake-backend tests cover these paths; native mutation
and a trustworthy boot-continuity source remain unadmitted.
An internal CO transport reuses the query session for full before/after vectors,
checks fresh capability and range evidence, and records possible submission before
the driver call. Forward edits also require the exact immediate reverse to fit
that range. Uncertain cleanup blocks later operations on the retained platform.
A concrete optional installed-stack provider now supplies the proof and connects
that transport to the live transaction. Construction performs no native work.
An initiating live request retains the existing device handle and immutable
inspected files. Each operation duplicates that same device file object and
rechecks host, processor, registration, controller and runtime admission. The
initial file inspection is reused only while its actual file pins remain held.
This assumes the trusted Windows signed-driver stack; disk hashes do not attest
the bytes of an already loaded kernel image.
It retains the actual native worker across caller cancellation and excludes
replacement operations on the same platform until completion. Cleanup uncertainty
invalidates that scope. Its private proof wrapper rechecks fresh context under
the native lease. A separate fixed CO metadata reader accepts only version 7 and
selector 5000A with strict unsigned fields. Its bounded version-7 compatibility
accepts Boolean true or completely enumerated absence of ReturnValue; the absent
form also requires the firmware shadow to lie within the magnitude bounds. It
preserves partial evidence, treats Current as a firmware shadow, and returns a
range only after confirmed cleanup and independent scope checks. Injected tests
exercise cancellation, ambiguous submission, range decoding and recovery.
The concrete proof captures its initiating admission, requires the forward and
exact reverse values to fit fresh metadata, and reads one complete getter pair
under each exact native write lease. Later requests cannot renew that proof.
Ownership contexts include the service instance and observed sleep/AC generations;
a restart or power boundary rejects reuse of an earlier ownership context.
The native factory retains one private platform for query, CO and PM operations.
An opaque typed session gives the service access without exposing raw driver
handles or commands. All three paths share exclusion before native acquisition;
uncertain resource or continuity cleanup keeps that platform parked. Construction
grants no authority, and each path still requires its distinct approval or proof.
An internal PPT transport now uses the same retained session for one typed fast
or slow command in milliwatts, with separate PM samples before and after it.
It checks both raw limit values and STAPM, rechecks the source-mapped conditional
fused cap and fresh firmware range, and requires independent admission for the
immediate reverse and every remaining original restoration value. Its proof must
establish that the candidate PM fields represent restorable requested settings;
integer reconstruction alone does not do that. A bounded static trace ties the
reviewed PM fields to the accepted post-clamp target and shows that the reviewed
maintenance paths preserve those targets. The native transport now requires a
fresh matching SMU firmware-version getter before baseline reads or setters.
Physical setter behavior and exact restoration still require the attended cycle.
The pure planner requires explicit
capability and firmware-range data for changes: capability 82 bit 1 bypasses only
the fused ceiling, while the firmware range still covers each forward and exact
restoration value. These inputs supply no freshness or native authority, and
independent admission remains mandatory. An unwired service transaction and
durable CAS journal now preserve the first exact Fast/Slow/STAPM pair across
repeated edits and partial restoration. Each step requires fresh full-pair
comparison and independent forward, immediate-reverse and original-restore
admission before its pending marker is flushed. Unknown submission, readback or
persistence outcomes retain uncertainty across restart; late completion cannot
advance the journal. Fake tests cover this orchestration.
Both live ownership observers persist confirmed drift, so returning values do
not restore permission after restart. Unavailable observations preserve the
existing journal. The PPT backend uses the same retained Service
worker as CO, including provider callbacks before native acquisition. Cancellation
and backend replacement cannot release that worker or clear cleanup uncertainty.
Its typed read-only observation facade distinguishes retained-installed approval
from attended RangeSingleSample approval and fresh run-owned driver lifetime; requested-state
interpretation and range admission remain independent provider requirements.
A Service-owned fixed metadata reader now decodes version 7 and continuous
selector 50001, preserving partial evidence and actual disposal outcomes.
Missing version acknowledgement compatibility requires the exact model/SKU
scope; MinMax accepts Boolean true or narrowly classified missing-acknowledgement
compatibility after this reader actually completes version 7, all getter stages,
cleanup and fresh local admission on the exact model/SKU profile. That eligibility
is consumed before the next continuous `50001` getter; an intervening getter or
failure cannot reuse it. Compatibility requires complete exact enumeration of all
five UInt32 fields, selector `50001`, an ordered interval, nonzero stepping and
Current within that interval. Its explicit `MissingReturnValueCompatibility`
provenance preserves raw `Absent`/null acknowledgement; it does not invent true.
The enclosing PM engine must still perform and record its separate fresh
post-admission before metadata can be complete. The attended r6 driverless read
completed both invocations and cleanup and returned Current 0, Min 0, Max 125000,
Stepping 1 and Value `50001`, with no ReturnValue. The old strict reader therefore
reported incomplete metadata. A subsequent attended test loaded the exact
published Capture default reader, including its real identity path. Both version
7 and MinMax completed with all getter stages, cleanup and separate fresh
post-admission checks. Both retained absent/null acknowledgements with explicit
compatibility provenance. This verifies the revised Capture reader's read-only
sequence. Current remains a firmware shadow,
and decoded bounds do not establish units, requested-state mapping or authority.
The optional already-installed driver path uses the same retained device and
file pins for CO and PPT. A separate typed installed approval binds each PPT
observation to its initiating request, fresh USB identity and runtime lifetime.
It checks firmware `0B5D1600` before copying the PM table, then obtains the fixed
version-7/`50001` metadata pair. It never creates temporary driver authority or
starts a driver. Default composition now includes PPT observation and its
durable ownership store; production PPT write proofs remain disabled pending
the reviewed physical setter fixture. This source change has not been installed.

The service runtime now dispatches injected live backends through retained CO
and PPT transactions. Each operation has admission tied to its initiating
session and power generation; later requests cannot renew an expired operation.
Uncertain submission or journal persistence blocks both live features, including
when a failed journal write leaves no entry on disk. Saved recovery remains
separate; confirmed saved identity or complete paired-image drift is durably
blocked, and saved journal persistence uncertainty remains blocked across
transactions in the same controller.

Combined live edits prepare both complete plans and their restoration routes
before either family sends. Each journal preserves its first original, including
across later single-family edits. Apply orders PPT before CO; restore orders CO
before PPT. Before every step the coordinator checks the other family's exact
expected state and restoration admission. Confirmed peer loss blocks the active
coupled journals, including when a peer has already been restored. An unchanged
family acquires no baseline ownership. An incomplete or uncertain operation stops
without compensation or automatic replay; verified partial changes retain their
independent recovery records. Each native step now carries its peer's immutable
expected state and first owned original. The native adapter reads the peer under
the initiating setter's same retained lease: version-bracketed accepted PPT
Fast/Slow plus STAPM for CO, or the full enabled-core CO vector for PPT. The
strict scripts retain firmware, capability, fuse, topology and deadline checks;
peer reads never reenter a service observer or acquire another native worker.
Exact-request proofs recheck peer restoration admission before the setter.
The installed provider permits an unchanged unowned PPT observation but continues
to deny PPT restoration/write authority pending the physical setter fixture.
These serialized observations do not make firmware state physically atomic.
Offline native and service fakes cover binding, drift, missing evidence and
reentry; this combined native sequence remains unverified on physical hardware
and does not establish physical live-write admission.
The IPC 60 status contract also permits PPT-only observations. These retain the
independent fast and slow limits in milliwatts and report CO as unknown. The tray
displays each limit separately, including fractional watts; it does not average
them into an effective value. Older IPC 59 peers cannot read this expanded status
contract, so deployment must update Service, Session Agent and Tray together.
Default composition shares one concrete retained-installed provider across CO
and PPT observation. Live PPT mutation remains unavailable.
The coordinated IPC 61 installation passed complete saved-firmware baseline
reads and typed service observation. First-failed-check diagnostics identified
an initial pre-submission rejection at fresh power admission ([issue #641](https://github.com/OpenBladeProject/openblade-core/issues/641)):
Windows reports unknown external power even on the barrel adapter. CPU admission
now uses the existing uncached adapter classifier and requires fresh agreeing
full-power readings. Battery, USB-C, unknown power, cancellation and changed
session or device state still reject commands ([issue #642](https://github.com/OpenBladeProject/openblade-core/issues/642)).

The corrected installed service passed attended CO disabled-zero with exact
original all-core −1 restoration, then a separate PPT 45 W cycle with exact
original-default restoration. Both complete 800-byte firmware copies matched
their originals after each cycle, and typed observation and recovery checks
passed. No live CPU settings, driver changes or reboot occurred. This validates
saved persistence for those transitions, rather than every selectable value.
Live controls still require trusted native proof and attended readback and
restoration. Boot activation remains unverified and separate from saved
persistence. The current CPU card remains a development UI; a production UI/UX
pass follows behavior validation.

The developer-only `--cpu-tuning-layout-smoke` harness renders seven synthetic
CPU states at two card widths. It checks saved/live separation, unknown live PPT,
pending boot changes, editing and restoration availability, and control bounds.
It writes screenshots to the private validation archive without connecting to
the service or sending CPU commands. These fixtures do not establish hardware
admission or validate the installed application.

Tracked in [issue #622](https://github.com/OpenBladeProject/openblade-core/issues/622)
and [draft PR #623](https://github.com/OpenBladeProject/openblade-core/pull/623).
