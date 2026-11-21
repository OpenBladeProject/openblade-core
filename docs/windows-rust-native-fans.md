# Native Windows fan slice

The optional `service-fan-mutation-v2` composition adds explicit linked
Fixed, Maximum and Automatic requests on the currently admitted native
RZ09-0581 / 1532:02E0 backend. The installed managed default is unchanged.
Other model catalogs do not grant this backend's actuation capability.

Fixed accepts 2,000–5,400 RPM in 100 RPM steps. Legacy Maximum retains its
own requested mode and resolves to 5,400 RPM. The 0D/01 target ACK proves
setter completion; 0D/82 independently proves mode and selector. Target RPM,
manual subtype, settled EC output and physical rotation are not readable here.
No 0D/81 getter or tachometer inference is added.

The codec, installed dispatcher and runtime use the existing serialized
ReadSource, Worker and Reader. Generic manual target changes send targets only.
Balanced and Performance entry preserve their admitted selectors; Performance
entry includes independent intermediate mode proof. An explicit Automatic
request that leaves saved manual fan intent goes directly to the saved
Balanced or Performance preset, as managed does: that preset's 0D/02 pair
carries the Automatic fan mode, so the stored performance intent is applied in
the same pair. Without such a preset, or with a saved GPU clock effect, it keeps
the observed selector. Under Silent, Battery Saver, Battery Balanced or Custom
selectors managed still hands manual fans back with a fan state pair under that
selector; native has no such pair, so with a saved GPU clock effect neither a
fan return nor a performance request leaves those manual fans (deferred).
Owned Stop cleanup writes only under the owner's selector,
on any external class: the handback commands are the same there. An explicit
Automatic request whose fans are already Automatic is a true no-op under the
destination selector, or under a selector or power class with no native
recipe, and never establishes ownership.

When a fresh, agreeing 0D/82 pair shows Automatic fans, owned Stop closes an
OpenBlade owner with progress 51 and no setter. That covers every stock
selector (Balanced, Performance, Silent, Battery Saver, Battery Balanced),
Custom with Automatic fans and HyperBoost (mode 7 with Automatic fans), on any
power source: Stop reads only the pair for this, not 07/8C. Managed does the
same: `ExactFanControlOwnership.ObserveTopology` clears ownership on any
confirmed Automatic fan mode, and shutdown cleanup writes nothing when fans
already use firmware Automatic. One difference remains: managed's read rejects
Custom High/High with Automatic fans (`MapThermalState` throws), so managed
keeps ownership there, while native Stop reads no Custom levels and settles
it. The 51 row repeats the owner's record,
including the source it recorded. The old selector is never written back first.

An explicit Automatic request closes the owner the same way when it has
nothing to write: the plan keeps the observed selector, or no native plan
exists (battery, Silent, Battery Saver, Battery Balanced, Custom). It needs a
decoded 07/8C source to resolve the saved intent: a classified class, or
unclassified external power (source 3) with fresh Windows AC presence (see
[unclassified external power](windows-native-thermal-mutation.md#unclassified-external-power)).
Its baseline read uses the same Custom getter as every native observation, so
a Custom reply publishes the levels it read. Custom High/High with Automatic
fans, which native and managed reads both reject, has no baseline: the request
is Blocked, saves no intent and keeps the owner, and Stop still settles it.
A saved Balanced or
Performance preset that differs from the observed selector on external power
writes its own pair and closes with 50. The reply names the firmware selector
only when it differs from the owner's recorded Balanced or Performance
selector, claims the saved preset only when the readback confirms it, and
otherwise says it was not applied. HyperBoost stays refused on this path:
its reply would publish an observation outside managed read parity.

Stop closes the owner only after fresh admission. Every power-source,
suspend/resume and session notice advances the lifecycle epoch that the
owner's own capture pins, so after an unplug, an adapter change, a lock or a
resume Stop uses a separate settle-only permit. It captures the owner's exact
SID, session and logon at the current epoch and rechecks origin and package
trust, the original custody and its generation, the unlocked console session,
the AC line status and competing-controller absence before each read. It can
append only the 51 row, so it cannot submit a setter; manual fans then keep the
owner outstanding. Nothing is captured while Windows reports suspend. The
permit's own half (owner and release flags, the per-read recheck, the saved
fan configuration, the 51-only journal and result admission) is covered by
readiness tests and mutants. Its Windows half (`WindowsGuard::capture_settle`
and `authorize_for`) is source-reviewed only: the tests drive a `SettleFresh`
model of it, not the real console, token, AC-line and controller suppliers.
Disagreeing or undecodable readback (including mode 7 with manual fans) keeps
the owner outstanding on both paths. So do manual fans under another selector at
Stop; an explicit return on external power writes the saved destination's own
setters and closes with 50. Without a destination there is no fan pair for
selectors 5, 3, 6 and 4, so it writes nothing, and an unsupported saved Custom
keeps the fans manual and says so, as managed does.

Known gaps in this handback, none of which can submit a write:

- HyperBoost restore. If firmware restores manual fans when HyperBoost ends,
  the owner was already closed at 51 while HyperBoost showed Automatic fans.
  Managed has the same exposure.
- Competing controller at Stop. The settle-only permit requires fresh
  competing-controller absence; managed's Automatic no-write branch does not
  (`ServiceControlRuntime.cs` returns on Automatic at :13648, before the
  controller check at :13653). With Synapse running, native Stop leaves the
  owner at 30. This fails closed and costs liveness only.
- Low power. Native detects suspend only from a delivered `PBT_APMSUSPEND`.
  Modern Standby may deliver none (`ServiceLifecycleRefreshLoop.cs:134-138`),
  and no notice arrives after `STOP_PENDING`. This predates the settle-only
  permit and affects the owner's own path too.

Other requests in Custom, Silent, battery and delegated states store intent
with a zero-setter pending software outcome until their separate native
recipes exist. No fallback profile or background control loop is added.

Every setter has fresh exact-device, caller/session, controller, lifecycle and
07/8C source admission. An unclassified reading needs fresh Windows AC presence.
Before anything is stored, a battery reading under Windows AC or unknown power
is blocked, and on classified power that agrees with Windows the request runs
managed's fan power policy and combination checks (see
[native thermal mutation](windows-native-thermal-mutation.md)).
A classified reading needs Windows AC that agrees with it, or Windows battery,
where managed admits a fan command unchecked (`allowBattery`); unknown Windows
power admits none. A return to Automatic that carries a saved preset needs
Windows AC throughout, as managed applies that preset through
`SetPerformanceAsync`; under Windows battery it is refused before Prepared and
writes nothing. It still stores Automatic, so a later return no longer leaves
saved manual intent and, as in managed (`previousManual`), carries no preset.
The reading must be in the operation's first class or another
external class whose destination is the same. A transition that writes the
Performance state pair without carrying a saved preset, owned Stop included,
stays in its first reading's class (managed acPreset with
`EnsureThermalWritePowerAsync`). A target-only change under manual fans and
Balanced have no such pin.
State-channel, state-to-target and target-channel spacing remain at least 10,
210 and 65 ms, alongside native feature settling.
The complete recipe and all command IDs are frozen before Prepared. Ordinary
preflight consumes four IDs. Remaining target-only, Balanced-entry,
Performance-entry and direct-Automatic budgets are 6, 10, 12 and 6 IDs.
Before any manual setter, another ten never-reused IDs are retained for normal
Stop's fresh reads, checks, direct Auto pair and independent final proof.
Ordinary observations cannot consume these retained IDs. The finite process ID
budget remains an unresolved production limitation; no reset or refill exists.

The fan history contract is `service-fan-mutation-v2/rz09-0581-linked-v1`.
It binds requested intent, complete selected recipe, source, selector, ordinary
and cleanup IDs to the original producer/request/user/session. Existing v1
performance rows, numeric meanings and raw commitments remain unchanged.
Fan progress uses 0 for Prepared, 10–17 for indexed setter intent/ACK, 30 for
mode-proven manual ownership, 31 for owned release preparation, 40–43 for
release intent/ACK and 50 for independent Automatic proof. The linked-v2 and
v3 contracts add 51: an owned 30 row closed because Automatic was observed,
with no setter. The row keeps the owner's recorded selector. Numeric 6 remains
Prepared-only KnownNotSubmitted after positive native retirement. Progress 30
completes the explicit request while retaining its firmware handback obligation.
Transport retirement alone cannot settle that obligation. Partial/future/foreign
contracts remain fenced. Original cleanup may resume after a later fully retired
Prepared-only request, using only its exact original record and retained IDs.
A partial later submission cannot authorize that resumption.

Normal Stop immediately denies ordinary writes, joins active original work,
then permits one bounded direct Auto release of the positively established
in-process owner. Expected Stop denies ordinary reads without discarding retained
custody. Unexpected origin or trust loss never becomes cleanup authority, and an epoch
change admits only the settle-only 51 close.
The established owner is kept separately from a candidate request, so a refused
identical request cannot discard its cleanup budget. Metadata-only settlement
can append only exact Prepared-to-KnownNotSubmitted evidence and grants no
firmware authority. Final result admission is separate from permission to submit
another command. Cleanup failure still permits mandatory resource retirement
and keeps firmware settlement unresolved. Fixed/Maximum sleep policy is unchanged.

Protected storage reserves the complete fan branch and retained prior cleanup
capacity before Prepared. The Rust and managed readers share canonical vectors,
original-owner/producer joins, global submission fences and history commitments.
The compatible rollback reader must ship first; an older incompatible reader
refuses new rows without deleting evidence. The new signed producer capability
is required; the original performance-only composition remains supported.

Validation for this slice is offline source, cached compilation and explicitly
injected/private-root tests. No installed-native execution, provider execution,
hardware observation, installation or sleep/resume test establishes this slice.
Curves, Stop guardian, duty control, cooling pad, broad automation, other models,
full Windows replacement and coordinated runtime/package cutover remain later work.
