# RZ09-0528 CPU Voltage Optimizer

The current product scope is Synapse's all-core CPU Voltage Optimizer: On/Off
with a remembered integer value from −30 to −1. Off requests zero while keeping
the editor's negative value. The editor sends only optimizer settings and keeps
saved firmware, next-boot intent and measured running offsets separate.

The driverless path saves firmware settings for the next boot. The optional live
path uses the exact signed AMD stack when it is already installed and running;
reads never start or provision a driver. Production composition admits live CO
on the exact model/SKU after complete observation, fresh safety checks and
resolved ownership. A saved value does not prove running state or activation
after restart. Installed tests have verified saved Off activation and restored −1
activation across two restarts. The installed all-core live −1 → 0 → −1 cycle
also completed with exact twelve-core readback/restoration and owned-resource
cleanup. That proof predates the setter-time peer correction below.
AC/battery/AC admission and lock/unlock recovery
passed. A sleep/wake failure was followed by a comparison with all twelve running
offsets independently measured at zero. The user reports repeated wake failures
with the cooling pad connected and successful Off sleep/wake when disconnected.
This is a user-observed association; its exact mechanism remains unresolved.
The captured saved −1 configuration was restored and verified. With the pad
disconnected, the final enabled-optimizer sleep/wake resumed normally on the
same boot, with saved −1 intact and no recovery pending. A read-only post-wake
observation confirmed all twelve running offsets at −1 and verified cleanup of
its owned temporary driver. The power-peer query was unavailable, so live-write
admission remained blocked; this read did not change CPU settings.

## Power-peer observation

The installed getter handles the exact sampling-prerequisite response `0xFD`
by closing the failed getter before one separately admitted observation. Each
getter retains its ten-second deadline; the explicit read-only sequence has a
21-second cancellation budget including a one-second wait. Hardware, boot,
runtime context and the retained installed lease must remain unchanged. The
replacement uses distinct getter approval and metadata. The empty first response
writes no journal, and mutation/preparation paths cannot consume this retry.
First-failure evidence and phase timings remain available.

One fresh-boot observation on the revised installed build encountered `0xFD`
and recovered in one request: 3,666 ms for the first getter, 1,009 ms waiting and
7,117 ms for the replacement. It read 80/75 W power peers and all twelve offsets
at −1 with live admission available and no recovery pending. No Set or Restore
was requested. The saved journal was byte-identical, validation processes and
the owned driver were closed, and the ordinary service resumed. This proves one
successful first-read recovery on the tested firmware, not arbitrary firmware
or native setter behavior.

Live PPT mutation and further power-limit experiments remain deferred. The
Voltage Optimizer card has no PPT controls or requests. Read-only PPT evidence
is retained for voltage admission and peer checks.

Admission is model/SKU scoped. BIOS 2.02 identifies historical captures; BIOS
provenance does not grant or revoke CPU admission or ownership. Firmware
interface version, complete layout, exact hardware, backend lifetime and
command-specific safety checks still apply.

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
exercise every selectable value or the installed application.

Later installed-service IPC cycles verified CO disabled-zero/original −1 and
saved PPT 45 W/original-default restoration. Both complete 800-byte firmware
copies matched their originals after each cycle, and typed observation and
recovery checks passed. These tests establish saved persistence for those
transitions, not stability or correctness at every selectable value. Boot
activation for saved Off and restored −1 was separately verified across restarts.

Recovery authority identifies its saved or live target and the features that
need restoration. A saved curve-only change does not require admission for an
unmanaged power limit, and saved recovery cannot enable a live restore. Missing,
malformed, drifted or uncertain recovery data blocks restoration. Every required
reverse command is admitted before the first restoration submission.

## Optional installed AMD backend

The optional backend is scoped to the RZ09-0528's exact signed AMD stack. Passive
inspection checks paths, signatures, hashes and service state. It never installs,
enables, starts, stops or removes a driver. Disk hashes do not attest the bytes
of an image already loaded in the kernel; the integration trusts the Windows
signed-driver stack.

The retained session confirms CPU node, SMN root bridge and topology separately
before PCI transport writes. Attended observations measured all twelve enabled
physical cores at −1, PM version `0x005D000B` and table length `0xD50`. The
private query helper's owned driver registration was removed after termination.
Read-only CPU queries still use PCI transport writes and require typed native
admission; they are distinct from CPU-setting writes.

An initiating live request retains the device handle and immutable inspected
files. Each operation duplicates that same file object and rechecks host,
processor, registration, competing-controller absence and runtime admission.
File inspection is reused only while actual pins remain held. One private
platform serializes query, CO and PM operations before native acquisition.
Caller cancellation does not release its actual native worker. Uncertain cleanup
or continuity parks the scope and excludes replacement operations.

The fixed CO metadata reader accepts version 7 and selector `5000A` with strict
unsigned fields. Completely enumerated missing `ReturnValue` compatibility
requires the firmware shadow within the magnitude bounds. It preserves partial
evidence and supplies a range only after cleanup and independent scope checks.
Fresh forward, immediate-reverse and original-restoration values must fit the
range. Later requests cannot renew an initiating proof. Ownership contexts bind
the service instance and observed sleep/AC generations; restart or a power
boundary rejects reuse.

Live journals preserve the first complete per-core original separately from
current edits and durably record possible submission before the native call.
Mixed and positive originals remain necessary for exact restoration even though
the editor exposes an all-core negative value. Confirmed drift is persisted;
returning values do not restore permission. Unknown submission, readback,
persistence or cleanup blocks further mutation without replay or guessed
restoration. Saved recovery remains separate.

### PPT metadata and voltage peer

The vendor parser and command implementation, corroborated by primary upstream
source ([UXTU](https://github.com/JamesCJ60/Universal-x86-Tuning-Utility/blob/eeb43042883bc9a80847999b2957b6ded86938a9/Universal%20x86%20Tuning%20Utility/Scripts/AMD%20Backend/RyzenSmu.cs#L224)
and [RyzenAdj](https://github.com/FlyGoat/RyzenAdj/blob/5775fc3e6dbb25c7030ee2d100a1bdd6e8bf2d0a/lib/api.c#L1470)),
map `0x32` to fast and `0x33` to slow. Initial captures contained 85/80 W,
while separate source-mapped fused registers decoded to 71 W. Those historical
values are not a current baseline or an unconditional live power ceiling.
Capability 82 bit 1 bypasses only the fused ceiling; firmware-range and
independent write/restoration admission remain necessary.

The exact firmware exposes PPT as a continuous-range entry. The Service uses
version 7 and `GetMinMaxSteppingValues` selector `50001`; the discrete
`Getdvalues` path is unsupported and cannot invoke WMI. MinMax accepts Boolean
true or narrowly classified missing acknowledgement after this reader actually
completes version 7, cleanup and exact model/SKU admission. That eligibility is
single-use. Compatibility requires exact enumeration of all five UInt32 fields,
selector `50001`, an ordered interval, nonzero stepping and Current within bounds.
Explicit compatibility provenance preserves the absent/null acknowledgement;
the enclosing PM engine still performs separate fresh post-admission. Attended
Capture validation completed this sequence with Current 0, Min 0, Max 125000 and
Stepping 1. Current is a firmware shadow; bounds alone do not establish units,
requested-state mapping or setter authority.

The installed observation checks firmware `0B5D1600` before copying the PM table,
then obtains the fixed metadata pair under initiating request, fresh USB identity
and retained runtime lifetime. Live PPT setters are absent from the product.
Voltage-only apply and restore require an unchanged read-only PPT peer, without
creating PPT ownership or granting a PPT setter. Native peer checks read accepted
Fast/Slow plus STAPM under the voltage setter's same lease before and after the
CO command. Missing or changed peer evidence blocks completion; possible CPU
submission remains journaled. These serialized reads are not physically atomic.
The current peer path has focused source/fake coverage. Its installed observation
returned complete power peers and all twelve offsets at −1 with live admission.
The subsequent cycle reached the 30-second submission deadline during its first
core's native execution. Submission is uncertain; forward readback and
restoration were not proved. Further writes remain blocked. Repeated full context
validation inside final checkpoints is tracked in
[issue #707](https://github.com/OpenBladeProject/openblade-core/issues/707).

An uncertain live submission keeps further writes blocked. An explicit Observe
may reconcile its ownership after a trusted new boot only when fresh complete
core measurements equal every captured original and complete current power
peers remain unchanged during the observation. The failed journal and readback
evidence are preserved before guarded settlement. Reboot or saved settings alone
cannot close the obligation, and this path submits no setter or restoration.
Installed validation verified this recovery after a restart: all twelve offsets
equaled the captured −1 originals and complete power peers remained unchanged.
The original failed journal was archived before settlement, with zero setters or
restoration requests. Independent review of the actual evidence passed. This
proves originals observed after boot; the earlier failed cycle remains failed.

Historical PPT recovery journals are retained as read-only compatibility data.
Invalid or non-Restored records block live voltage apply and restoration.
A completed Restored record permits read-only observation; no record is rewritten
or deleted, and there is no live PPT restoration command.

## Presentation and validation

The editor initializes from confirmed state without overwriting user edits on
refresh. Mixed per-core observations require an explicit choice before Apply.
Commands retain target/settings through deferred startup. The card suppresses
restoration that includes PPT. Live write availability requires complete
observation, resolved ownership and fresh admission; every setter and reverse
still needs its own native proof. Captured −30 to −1 capability and zero for Off
do not prove stability at every offset.

CPU admission uses the uncached adapter classifier and fresh agreeing full-power
readings. Battery, USB-C, unknown power, cancellation and changed session/device
state reject commands. PPT-only status can retain independent Fast/Slow
milliwatts while CO is unknown; the card never derives CO from those limits.

The CPU branch uses IPC 60; the installed combined CPU/cooling build uses IPC 61.
A CPU-branch build alone is not an installation candidate for that combined
setup. Deployment must retain cooling sources and update Service, Session Agent
and Tray together when shared contracts change.

The developer command implementations and deployment broker are split into
separate PRs. Historical evidence remains in the preserved original branch
`archive/cpu-tuning-full-34ff4948` and the private validation archives.
