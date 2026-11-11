# Native origin/status correction contract

This offline Windows migration stage corrects the selected LocalDev native path.
Managed Setup, Service and Session Agent remain the default. Firmware acquisition
remains denied; this stage neither activates native roles nor admits hardware work.

## Protected paths and trust changes

Only the fixed shared ProgramData ancestor admits ordinary metadata grants
WEA/WA along with WD/AD (0x116). Product root, broker/private parent, token,
replacement, delete-child, delete, WRITE_DAC and WRITE_OWNER policies stay strict.
Shared canned descriptors establish policy compatibility, not a survey of stock hosts.

Enrollment changes pin/refuse any preserved legacy store even when the public
root is absent. The production upgrade seam checks native history before
maintenance disable/stop or byte replacement and checks again after quiescence
under existing maintenance/deployment serialization. A later failure retains the
existing disabled-maintenance behavior.

## Selection and original service attempts

Setup inspects the retained root under the epoch writer. Verified bootstrap has
an exact root marker, no current/frontier, empty recognized owner history and
only known bootstrap names. Begin repeats this admission under its own writer.
Missing current with reservations, mappings, unknown files or ownership cannot
reset history. An interrupted exact frontier authenticates its signed package
before existing metadata recovery reconciles that same attempt.

New selected packages require the signed service feature startup-custody-v1.
The fixed metadata-only service entry recipe binds startup-policy.bin to the
origin, package and service artifact. Setup durably publishes submission.bin
after service configuration and immediately before the possibly submitted StartService operation. Every service boot/restart holds the
same exclusive attempts/custody.lock, rechecks selection after acquiring it,
and durably creates its original PID/creation/session/token-role/package/image
launch record before dispatcher/factory. The lock remains held through cleanup
and process-origin lifetime. No record is overwritten or deleted.

Actual listener/worker drain creates a v2 acknowledgement bound to that launch
digest. Stopped/interrupted recovery acquires the same custody, validates every
launch/drain pair and separately verifies retired firmware-owner history. No
launch can prove no admitted metadata work only for this enforced signed entry
contract under custody. SCM Stopped, absent PID, process exit and arbitrary
markers do not certify drain. Contradictory committed readiness, missing drains,
partial/unknown records, active custody and unknown firmware owners refuse with
typed reasons. Legacy artifacts without this feature need the existing retained
live original-process stop/exit and actual v1 drain; stopped legacy state is
preserved as unresolved.

Retirement publishes only package/metadata state. Pending startup cannot enter
after retirement under custody. A new install allocates a fresh GUID epoch and
ordinal while retaining every old mapping/reservation. If native transition
fails after possible predecessor retirement, Setup preserves explicit recovery
instead of generic service/startup restoration. Readiness failures after possible
submission still prohibit automatic fallback. Stage -> service retirement ->
Agent disposal/writer release -> Begin remains the order; uninstall removes the
Service before stopping Agent. Future firmware composition must define a new
reviewed custody/drain contract; this metadata-only proof cannot be reused for it.

## Recovery and lifetime correction

The next finite stage starts from e1c6aed9. The platform carries the actual
predecessor kind and preparation outcome. A verified managed predecessor can
restore its captured fixed path and running/stopped lifecycle before incoming
native startup. The incoming transaction never selects the rollback image.
Native retirement, missing historical drain and uncertain incoming startup retain
explicit recovery; retired generations are never restarted.

Only attempts/custody.lock gets a protected SYSTEM/Administrators-only DACL,
created before publication. Both readers require that private policy. Public
attempts, Agent evidence and neighboring files retain their existing access.
Legacy public-custody generations are refused and need explicit replacement;
Setup does not change an uncertain old lock's ACL. This addresses a conflicting
unprivileged read handle on that lock, not general local denial of service.

Under the epoch writer and exclusive custody, recovery recomputes the complete
current attempt set and selected-phase commitment. Immutable snapshots live at
retirement-proofs/<sha256>.bin. Each ensuing retirement publication authenticates
the fresh snapshot; older snapshots never certify a later history. Preserve all
snapshots and bounded canonical unpublished receipt-<guid>.tmp staging files.
That directory has a separate 16384-entry bound, with 2048-byte snapshot/staging
limits. Retirement intent fences native startup before frontier/current updates.
Interrupted retirement publication also requires fresh authenticated custody.

Both languages use a 4096-attempt budget: one custody file plus 4096 complete
launch/drain pairs uses 8193 entries. Rust checks the full bounded history under
custody before creating another launch, reserving its drain slot. Exhaustion
refuses another launch but allows the admitted drain and fully drained generation
retirement. Oversized or malformed history is preserved and refused. This trades
another restart for explicit generation replacement.

Recovery reports bounded typed reasons with original causes for missing/malformed
history, active/unknown custody and failed/conflicting retirement publication.
Cancellation and existing typed refusals retain their original meaning.

STOP/SHUTDOWN revocation is typed separately from sticky trust loss. Both stop
publication immediately. Ports normalizes expected STOP only after the real
shutdown signal and before combining serving and retirement outcomes. Close,
worker drain, acknowledgement and SCM-report failures remain failures; lost
shutdown is not an orderly stop. A sampled trust loss dominates a later STOP.
GetStatus receipt is timestamped before waiting on the observer mutex so a refresh
that completes after receipt can satisfy the request; later GETs still require
fresh completion.

Historical unmatched launches still refuse retirement, even after a later exact
original drains. Current startup markers do not encode an independently enforced
metadata-only capability excluding hardware-capable compositions. A separate
versioned authenticated capability/recovery contract is the next stage; no old
attempt is abandoned, reset or inferred drained here. Actual thermal observation
remains a later independently admitted step.

## Peer availability and exact Windows strings

Readonly Service acquisition validates origin, signed package and fixed SCM
configuration separately. Configured StartPending/StopPending/Stopped return
Unavailable. Unknown states, query/access failures, Running/PID0 and actual
identity/configuration mismatches are Denied. A retained generation waits on its
original object: confirmed exit is Retired, temporary unavailability is not
latched, and actual denial remains sticky. Connections reacquire fresh authority
using the existing bounded cancelable backoff; partial responses are discarded.
This says nothing about an external supervisor's relaunch behavior.

New startup-owner v2 records encode exact UTF-16LE code units as canonical base64
(up to 4096 units), preserving non-Latin, non-BMP and unpaired surrogate units.
Rust decodes to OsString before binding the original process image. v1 is read-only
compatibility with a literal printable ASCII final field; its bytes are never
reinterpreted as base64. Invalid padding, odd byte counts, NUL and excessive data
deny. Readiness v2 uses the same path encoding. Shared vectors exercise both codecs.

## Evidence boundary

Pure, injected and private-filesystem tests exercise these production-used seams.
Exact native role binaries, required Rust core, default managed/selected builds
and the scratch NativeAOT receiver are compiled and retained with source/hash
provenance in the handoff packet. Compiling them does not establish live SCM,
process/token, installed ACL, pipe, task, module-loading or hardware behavior.
No product or fixture role is launched in this stage. Static CRT guard and all
rejected-claim dispositions remain unchanged. Remaining full Windows operations,
background loops, model parity, journals, packaging/cutover and controlled runtime
validation remain outstanding; Linux, macOS and SwiftUI are deferred.
