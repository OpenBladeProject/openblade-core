# Cooling-pad service integration

Issue #119 and draft #621 now include a separate cooling-pad settings and
status path. The installed service's native admission remains closed. This
does not enable production control or establish measured fan RPM.

The service owns transient pad intent independently of laptop fan profiles:
Stop, fixed configured demand, or independently evaluated CPU/GPU curves.
Settings last until the service restarts; they are not saved in power profiles.
The tray exposes an observed capability and separates requested settings,
the latest planned demand, and historical exact acknowledgement. It never
fills laptop fan readings with pad demand values.

The supported developer identity is host model/SKU `RZ09-05289EN4` and pad
`1532:0F43`, revision `0200`, MI00, usage `000C:0001`, 91-byte Windows Feature
buffer with zero prefix. Model/SKU and exact device geometry control admission;
BIOS versions are provenance only. Production composition constructs no native
pad backend. Developer construction is explicit and cannot be selected through
IPC, a profile, or a saved evidence artifact.

The coordinator stages settings before explicit Resume. Resume starts only
from a freshly queried raw `0000`, then acquires/queries `0300` and sends the
captured entry immediately followed by a fresh demand. Raw state is not
fan-state readback or proof of firmware ownership. In an admitted running
session, changing settings applies on the next maintenance tick without
reacquisition. Unchanged targets are suppressed against the current session's
last exact acknowledgement. The existing worker attempts pad maintenance every
second one-second timer tick, a nominal two-second cadence. Laptop maintenance
or other awaited work can delay that tick; each pad update acquires a new sample.

Curve planning uses the larger of the independently calculated CPU/GPU
demands, endpoint clamping and upward 50-RPM quantization. Host acquisition
age includes the read and must remain below five seconds, including a check
after the controller guard immediately before native submission. It does not
provide a sensor-origin timestamp. CPU input is the existing firmware thermal
zone reading, not an independently measured CPU-core temperature.

The current GPU inactivity heuristic lacks counter-coverage attestation.
Consequently this coordinator does not treat an unqualified inactive result
as permission for CPU-only planning. Missing, invalid or stale telemetry
pauses future writes, preserving historical acknowledgement as history.
Unknown activity or an unavailable active-GPU temperature also pauses.

Disconnects, conflicts, cancellation and ambiguous exchanges revoke the active
epoch. A different user's authorization cannot revive an old pending SET.
Reconnection never automatically replays intent; explicit Resume checks fresh
admission. The current raw `0000` prerequisite can block Resume after a prior
session left `0300`; this is reported instead of guessed recovery.

Stop ceases updates and disposes resources. The pad may retain its demand.
Stop, telemetry failure, shutdown and low power send no quiet command or
firmware handback. The attended developer trial has a separate explicit
Medium-to-quiet restoration sequence on the same still-owned session; it
cannot run after the session has become uncertain or been disposed.

Native exchange waits and disposal are bounded. Late operations remain owned
until actual completion, and a pending or failed disposal blocks replacement.
Every native SET has fresh exact metadata/controller checks and revocation
checks before and after the caller's admission predicate. No retry follows
uncertain submission. Four transaction IDs are reserved for explicit developer
restoration; this finite validation session allows at most 246 demand updates
and does not recycle IDs. Continuous production transaction management remains
a separate admission prerequisite.

Service IPC advances from 58 to 59 for the typed settings and Resume requests.
Protocol51 read-only compatibility and inactive-session projections omit pad
intent/status. Profile, export and override schemas are unchanged. Shared
Contracts changes require a coordinated installation rather than a tray-only
patch. Product version advances from 0.23.0 to 0.24.0 for the new visible
settings/status capability; this version does not admit native pad writes.

The next hardware validation is the finite service-curve trial described in
[the attended guide](cooling-pad-service-curve-trial.md). It must exercise this
coordinator and adapter, preserve source/build identity, and record actual
acknowledgements and operator observations. A warm-up/cool-down result does not
independently establish RPM, thermal causality or firmware Auto regulation.

The trial exercises the core coordinator and adapter, not the outer installed
service admission or IPC. Low-power, active-session and confirmed laptop
HyperBoost admission remain outside its scope; HyperBoost inactivity is an
attended operator requirement throughout. A successful trial cannot grant
production admission.
