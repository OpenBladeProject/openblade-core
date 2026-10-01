# Cooling-pad service control

This implementation adds installed-service fan control for the exact RZ09-0528 /
RZ09-05289EN4 host and cooling pad `1532:0F43`, revision `0200`, MI00,
usage `000C:0001`, 91-byte Windows Feature geometry with report prefix zero.
Admission uses model/SKU and device metadata, never BIOS version. It requires
fresh competing-controller absence before every SET. The installed IPC path
still requires the attended acceptance check below; implementation and unit-test
results are not a completed hardware validation.

## Using fan control

Choose **Fixed demand** or **Host curve**, then **Use settings** and **Resume
updates**. Settings apply to this service session and are not persisted as power
profiles. Startup defaults to Stop and sends no pad commands. Requested settings,
planned demand and historical acknowledged demand are displayed separately;
none is measured fan speed.

Configured demand uses the vendor's 500..3200 RPM-unit range in steps of 50.
The encoder and range are source/capture backed. Full-range physical behavior is
not validated; the reviewed native trials cover a narrower set of demands.
The capability retains `FullDemandRangePhysicallyValidated=false`.

Host curves independently interpolate CPU/GPU points, clamp endpoints, round
up to a 50-unit step and choose the larger demand. Maintenance uses the existing
nominal two-second scheduler; other service work can delay it. Every new demand
requires a sample acquired less than five seconds ago, rechecked after the
controller guard immediately before SET. This is host acquisition age, not a
sensor-origin timestamp. CPU is the existing firmware thermal-zone reading.
An unqualified GPU-inactive heuristic cannot silently authorize CPU-only control.
Missing, invalid, uncertain or stale telemetry pauses updates. A healthy session
is parked, and explicit Resume requires fresh telemetry before another demand.

**Stop updates** sends no quiet command or firmware handback. It revokes queued
admission and parks a healthy owned session after its current operation settles.
The pad may retain its last demand. Choose new settings and explicitly Resume to
update that same session with fresh guards; no reopening or reacquisition occurs.
Changing settings during Running applies at the next admitted tick. Only the
current session's last exact ACK can suppress an unchanged target.

## Restoring preferred quiet

On a healthy running or parked session, read the physical prompt and check the
confirmation box, then select **Restore quiet / confirm stage**. Keep watching
fan behavior and lighting during confirmation and restoration. The initial
confirmation means the current condition is normal; it is not a measured RPM
snapshot. A changed acknowledged demand resets the checkbox. The stages are:

1. Confirm normal current fan behavior, unchanged lighting, no red/sleep
   indication and inactive HyperBoost; request captured Fixed Medium `01012C`.
2. Confirm normal Medium behavior and the same lighting/HB conditions; request
   captured preferred-quiet `000600`.
3. Confirm preferred quiet and normal lighting/HB conditions; restore the saved
   raw0000 control state and require the matching query.
4. Confirm preferred quiet remains normal; resolve this operation's durable
   evidence and close its session.

Begin confirms the current normal condition under continuous operator watch and
creates the operation ID after admission. Subsequent replies bind to that issued
user/session-bound operation ID, expected stage and ten-minute monotonic deadline.
A confirmation is consumed once. No checkbox or
confirmation is carried to another stage or reconnected UI. Cancel, rejection,
expiry, session change or ambiguity stops later stages. There is no automatic
restoration in cleanup, shutdown or error handling. Raw0000 is mode evidence,
not fan-state or ownership readback. Quiet is an operator observation, not a
claim that firmware now runs an autonomous temperature curve.

## Ownership and failure behavior

The service and native developer pad openers share a protected machine-wide
cooperative lease. The service retains its ownership reference after pad Stop;
individual native-session references survive delayed disposal until actual
FullyDisposed release. HyperBoost entry also needs this lease. Known older
Capture processes, USBPcap and vendor controllers block production admission;
relevant Razer services must be exactly Stopped. OpenBlade does not stop them.
This lease excludes participating OpenBlade processes, not arbitrary vendor or
administrator software. Fresh vendor checks remain necessary.

The exact-pad production policy uses one outstanding full-response exchange and
wire IDs `01..1E,00..1E`. Queues and diagnostic state remain bounded; there is no
finite developer-operation ceiling or accumulated unbounded history. Checked
logical ACK counts exclude assignments rejected before SET. Ordinary allocators
and default zero rejection are unchanged. The historical developer experiment
retains its separate 39+4 capacity and its source-bound evidence.

A proven admission revocation before native SET consumes that assignment without
replaying it. Only an already-entered healthy production session may then park
for a later explicit request. Partial startup, uncertain submission, unexpected
response, identity/controller failure and incomplete disposal never use that
exception. There is no automatic retry, reopen, reacquire or counter reset.

The protected private journal records a pending mutation before a possible port
setter and checkpoints exact acknowledged outcomes afterward. It distinguishes
pending, acknowledged retained, parked, restoration-in-progress and resolved
states. Generation/hash comparison prevents stale replacement. Settings and
metadata alone do not arm it. Crashes between submission and durable outcome,
corrupt/inaccessible storage, failed persistence and ambiguous completion block
fresh control and HyperBoost entry. HyperBoost exit remains available.

A blocked HyperBoost entry parks a healthy retained pad session so the user can
still explicitly restore quiet. It does not erase the retained effect or revive
an earlier queued SET. Power transitions, disconnection, competing controllers,
active-session loss and shutdown revoke and dispose without firmware cleanup.
No connection, power or process transition automatically resumes demand updates.

After service restart or a disposed/disconnected epoch, the UI shows retained
acknowledgements as history. The stored device scope identifies a class, not a
uniquely serialized physical pad. Ordinary Resume and quiet restoration cannot
use that record to open a replacement session. Clean retained-state takeover and
distinct uncertain-state attended recovery remain closed: their new fresh-handle
composition has not been validated. Do not clear the journal or interpret raw0000
as recovery. Same-process healthy Stop/Resume is the supported resumable path.

## Compatibility and remaining attended acceptance

Product version advances from 0.24.0 to 0.25.0 for this capability; the current PR
compares main 0.23.0 to 0.25.0. Service IPC advances from 59 to 60 for typed
restoration requests/status. Protocol51 read-only projections continue to omit
pad intent/status. Profiles, exports and override schemas are unchanged. The
private mutation record writes schema2; legacy schema1 records load
conservatively, and unresolved records are never automatically migrated clean.
Older binaries fail closed on new records. Shared Contracts require a coordinated
installation, not a tray-only patch.

Before production acceptance, use the reviewed exact installed build through
normal service IPC with the operator present: fresh quiet/green/HB-inactive
baseline, a bounded changing-demand/curve interval, Stop then explicit Resume,
blocked HyperBoost entry retaining restoration availability, and the explicit
Medium/quiet/raw restoration prompts. Observe low/high vendor-range endpoints
under a separately agreed scope; software encoding tests do not prove their
physical behavior. Preserve source identity, actual ACKs and operator checks.
Do not start a workload or vendor controller automatically.

A later clean retained-state takeover check must be prepared as an explicit
operation: exclusive new ownership, fresh exact guards/raw query, raw0000
acquisition or an explicitly authorized raw0300 entry path, then fresh requested
demand. Neither branch may replay saved demand or claim a unique-pad match. This
specific path remains unavailable pending implementation review and attended
validation; unknown outcomes need a separate explicitly attended resolution,
not an ordinary Resume bypass. No further lifecycle matrix is required to prove
software cancellation and storage failures; deterministic tests cover those.

The earlier finite core trial and repeated-ID result remain separately recorded
in [the service-curve guide](cooling-pad-service-curve-trial.md) and
[the counter guide](cooling-pad-counter-reuse-validation.md). They do not test the
outer installed-service IPC admission. Lighting remains a separate issue119
requirement. This change makes no measured-RPM, thermal-tracking, firmware
ownership or complete issue119 claim.
