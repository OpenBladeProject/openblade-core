# Bounded installed native thermal mutation

This opt-in source composition extends accepted native mailbox observation with
`service-thermal-mutation-v1`. It does not select, install, run, or make the
native backend the default. The normal publisher and installed product remain
managed. Native Windows suppliers are compile-only during this stage's offline
verification; neither software fixtures nor compiled suppliers establish live
Windows or physical hardware proof.

## Reachable composition and authority

The compiled service artifact asserts the exact ordered capabilities
`private-service-custody-v1,service-mailbox-observation-v1,service-thermal-mutation-v1`.
The native artifact producer, native package consumer, managed artifact parser,
selection and Setup handoff consumers retain the same authenticated mailbox
handoff requirement. A correctly authenticated role inventory can carry this
composition. There is no new default native publishing or installation path.

The installed v85 dispatcher accepts typed `setPerformanceOverride` and
`reportApplicationContext` requests only in this composition. The SID comes
from the actual named-pipe peer token, with active interactive session continuity
checked before and after the operation. The real supplier independently matches
that SID/session/authentication LUID against the current Windows session token.
It checks the loaded committed origin, competing-controller absence and known
coarse power. Actual opened-port validation checks the reviewed exact RZ09-0581
host/HID identity. Per-command 07/8C readback classifies adapter sufficiency.
SCM power/session ingress other than a power-setting read advances a separate
lifecycle epoch and fences old operations while preserving loaded-origin
custody. Only STOP/SHUTDOWN or actual trust loss is terminal. Suspend sets the low-power gate, which blocks mutation
until a resume clears it; session events cannot establish resume. Fresh exact
WTSInfoEx active/unlocked session admission is required for mutation. The gate
starts clear, as managed does; display-off alone is not confirmed low power.

The existing ReadLease owns the managed handoff exclusion and the single durable
owner Store. It drains its actual native reader before publishing the genuine
first Reserved then Active records. Later epochs require an actual Retired
predecessor. It neither opens a second owner Store nor fabricates a first Retired
record. Only the process that activated that Store can publish its retirement.
The older hardware-compile prototype authority remains unavailable and is not
on this installed route.

A restarted, already consumed epoch can obtain process-bound read-only recovery
custody after acquiring the original exclusion and owner writer handles. Its
separate recovery acquisition/retirement records do not certify the original
process's retirement and cannot activate a writer. Valid unresolved owner history
remains readable and prevents a new mutation reservation.

### Low-power gate

`openblade_domain::low_power_gate` ports managed `LowPowerGate`
(`LowPowerGate.cs:3-133`) as pure state over injected QPC timestamps. The
generation's low bit is the latch and each boundary adds one. A signal older
than the latest accepted one is stale, and a suspend at the latest timestamp
cannot undo a clear. A resume more than two seconds after the last boundary
adds two, unless a resume recovery retains that generation. DisplayOn clears
the latch but never re-enters it. `LowPowerGateNativeVectorsTests` records
`low-power-gate-vectors.json`, and the domain test `low_power_gate_vectors`
replays it. The two-second window is computed exactly. Managed scales by a
`double`, which can differ only within one tick of the edge, and only on a
clock whose frequency does not divide ten million.

The shipping epoch `Lifecycle` (`origin_lifetime.rs`) holds the gate under the
lock that guards the epoch. The gate replaced the former `awake` flag and is the
only low-power state. `notice` stamps each SCM callback with
`QueryPerformanceCounter`. `signal_for` maps a power event's
PBT_APMSUSPEND to `Signal::Suspend`, PBT_APMRESUMESUSPEND or
PBT_APMRESUMEAUTOMATIC to `Signal::Resume`, and a console display read from
off to on or dimmed to `Signal::DisplayOn` (see Power-setting notifications).
A session notice never signals, whatever its event number. Every power and
session notice but a power-setting read advances the epoch, a stale signal
included. Display-off, other display changes and AC/DC notices carry no signal
and do not fence (see Power-setting notifications). The
5 s duplicate-resume window decides managed dispatch, not gate state; it
belongs with the refresh dispatch in C3.

`epoch_for(WriteScope)` is the low-power clause of managed
`ScopedThermalWriteBlockReason` (`ServiceControlRuntime.cs:15410-15421`). Only
that scoped thermal clause is ported. Managed runs other cleanup while the gate
is set, under its own admission: the fan-stop release
(`ServiceControlRuntime.FanStop.cs:319-339`) and the keyboard Driver-mode
release. Neither is a `WriteScope`. The gate refuses `WriteScope::Ordinary`
while it is set. `active()` is `epoch_for(WriteScope::Ordinary)`. Every capture reads it, as do
`capture_settle`, mailbox custody and status, so nothing is captured in low
power. `WindowsGuard::authorize_for` compares the pinned epoch with
`epoch_for(WriteScope::Ordinary)` before and after its Windows checks, so
per-write admission stays authoritative. `WriteScope::LowPowerFanAutomatic` is
managed's `PriorAuthorityCleanup` exception for the low-power preparation's
firmware Automatic fan handback. The gate admits it only while the exact suspend
generation it captured is current (`IsActiveForGeneration`), so the next
resume, DisplayOn or suspend ends it. Nothing captures that scope yet. Owned
Stop and shutdown handback stay Ordinary, because managed blocks them in low
power too. D1 decision: `epoch_for(WriteScope::LowPowerFanAutomatic)` also pins
the epoch, so a later power-status or session notice fences the cleanup. Managed admits
it on the generation and ownership only. D1 must either keep that stricter
fence or recapture after such a notice, and record which. `low_power()` returns
the current transition for timers that ask outside a write, and `None`
(unreadable or exhausted) means low power. Timers that need only the answer
use `low_power_or_unknown()`, which is true for `None`.

### Power-setting notifications

Managed subscribes to suspend/resume, `GUID_ACDC_POWER_SOURCE` and
`GUID_CONSOLE_DISPLAY_STATE` with `DEVICE_NOTIFY_CALLBACK`
(`WindowsServiceLifecycleEventSource.cs:571-648`). The native Service
registers the two settings against its SCM status handle with
`DEVICE_NOTIFY_SERVICE_HANDLE` instead (`installed/power_notifications.rs`).
Each change then reaches the one SCM handler as `SERVICE_CONTROL_POWEREVENT`
with `PBT_POWERSETTINGCHANGE` and the same `POWERBROADCAST_SETTING` payload
managed reads, ordered against every other power and session notice. Suspend
and resume need no registration: `SERVICE_ACCEPT_POWEREVENT` already delivers
them. Managed's HID interface registration is a device trigger, not a power
notice, and is not part of this supplier. Only the thermal composition, which
accepts power events, registers.

`power_notifications::Supplier` places both against the SCM status reports.
It registers right after `SERVICE_RUNNING` is reported. Windows sends each
setting's current value when it is registered. SCM is expected to drop a power
event the current status does not accept, and `SERVICE_START_PENDING` accepts
none, so a registration during startup could lose the initial display state;
that drop is unverified and attended-only. After the RUNNING report the
initial value reaches the handler and the display state starts known, as
managed's does; until then it stays unknown and is never guessed. A
registration failure unregisters what it registered and fails that report.
Because native reports RUNNING before it registers, SCM then sees RUNNING
followed by STOPPED (failed). Managed's constructor throws before RUNNING is
ever reported, so it shows only the failed stop.

The Service unregisters in managed's order (power source, then console
display) before it reports `SERVICE_STOPPED`, because SCM may end the process
once it reads it. Both are attempted even if one fails, and the first outcome
is kept. An unregistration failure does not change the stop's own status:
managed's `WindowsServiceLifecycleEventSource.Dispose` rethrows it only at host
disposal, after `WindowsServiceLifetime` has reported STOPPED, so SCM sees the
stop's outcome. A failure exit there would make the restart actions setup
configures (`WindowsSetupPlatform.cs:461-465`, failure flag 1) restart an
intentional stop. The Service has no native diagnostics sink yet, so the
failure is kept for the caller and then dropped; the process exit releases the
registration. `Drop` covers an exit that never reached the report. The
registration calls sit behind an injected `Api`; tests never call the real
one.

`scm_host::control` forwards `event_data` only for a power-setting change. It
copies the header and at most one DWORD of the payload `DataLength` declares,
byte by byte, then `openblade_domain::power_setting::decode` ports
`TryReadPowerSettingDword`: a registered GUID, `DataLength` of at least four
and a DWORD of 0, 1 or 2, otherwise nothing. Managed has one callback per GUID;
the shared handler dispatches on the GUID instead. A refused payload is still a
power event, and the installed hook still samples the power kind.

A power-setting read does not advance the lifecycle epoch. Managed fences
nothing on a display read, which only toggles lighting suppression and
HyperBoost display-off preparation. It does fence the AC/DC read: that
callback is its only `PowerChanged` publisher
(`WindowsServiceLifecycleEventSource.cs:695`), and
`HandlePowerSourceChangedAsync` bumps `powerAdmissionEpoch` and
`pendingPowerAdmissions` (`ServiceControlRuntime.cs:12327-12328`), which gate
HyperBoost, drift validation, convergence, fan-curve maintenance, fan
recovery, the cooling pad and fan-stop entry. Native's fence for that source
change is `PBT_APMPOWERSTATUSCHANGE`, which still advances the epoch, so the
setting read would only fence a second time. That duplicate abandoned work
captured between the two notices; a temporary-full-charge cancel lost that way
on battery was never retried. That Windows sends `PBT_APMPOWERSTATUSCHANGE`
for every AC/DC switch is attended-only. Suspend, resume, the other power
events and session changes still fence. The read still updates the display
state and can still signal `DisplayOn`.
`classify_console_display_transition` ports `ClassifyConsoleDisplayTransition`
exactly. The display state lives under the lifecycle lock, starts unknown and
changes only on a valid read, as managed's `consoleDisplayState` does. AC/DC
needs no decoded value: managed's callback publishes `PowerChanged` without
reading it, and the existing `observe_power_kind(sample_power_kind())` runs on
every power event. `PowerSettingNativeVectorsTests` records
`power-setting-vectors.json` (the classifier, the decode with malformed rows,
and the notice-to-signal mapping), which the domain test
`power_setting_vectors` and the host's SCM payload test replay.

A Modern Standby display-off sends no `PBT_APMSUSPEND`, so the gate stays
clear and nothing is fenced, as in managed. A later off-to-on read clears a
suspend whose resume was missed.

What the remaining lifecycle work needs from this supplier:

- C1 (automatic power-source transitions) needs no time debounce. Managed
  coalesces repeated notices into one pending `PowerChanged` bit in a bounded
  channel. `HandlePowerSourceChangedAsync` first bumps the power admission
  epoch, then keys the transition on the resolved class change
  (`ChangePowerSourceCore`, `ServiceControlRuntime.cs:12351-12352`), not on
  the Windows power generation. The C1 port must keep that fence: native gets
  it from the `PBT_APMPOWERSTATUSCHANGE` epoch advance, and C1 must not drop
  it or move it onto the setting read.
- C2 (display-off work) needs the off transition that
  `openblade_domain::power_setting::power_event_signal` drops
  (`power_setting.rs:130-132` maps only `DisplayTransition::On` to a signal).
  Managed uses `DisplayOff` for lighting suppression and HyperBoost display-off
  preparation; it must not become a gate signal.
- C3 (resume recovery) needs managed's resume dispatch on `DisplayOn`: when
  the gate was set, `Publish` queues the same deduplicated resume path as a
  missed `PBT_APMRESUMEAUTOMATIC` (`WindowsServiceLifecycleEventSource.cs:363-368`).
  Native clears the gate but dispatches nothing yet.

The unwired lifecycle worker in `windows/scm/lifecycle.rs` was retired rather
than wired. It kept its own `Activity`, display state and resume window, and
nothing in the installed Service consumed it; wiring it would have made a
second low-power state beside the gate. The file now keeps only the read-only
power evidence that `installed/status_source.rs` includes. Its development
metadata fixture went with it.

Attended validation still needed on a live Service: both GUIDs are delivered
to the SCM handler with this registration; SCM drops a power event sent while
START_PENDING; the initial value of each arrives after the RUNNING report;
every AC/DC switch also delivers PBT_APMPOWERSTATUSCHANGE; a Modern Standby
entry and exit produce display notices and no suspend, and leave the gate
clear; and the gate clears after resume from both Modern Standby and S3,
including when the resume is missed and display-on recovers it.

## Saved intent and the bounded compatible slice

An existing protected ProgramData/OpenBlade/users/SID directory is selected only
after authenticated SID/session binding. SYSTEM or Administrators ownership and
safe inherited ACLs are admitted; untrusted modification or child replacement is
not. No legacy-root discovery, owner guessing, user-root bootstrap, or default
profile materialization occurs. The native writer retains its own exclusive
configuration writer handle, protected relative paths, atomic replacement and
independent byte readback.

Manual overrides at schemas 8, 9 and the current 10 support Silent, Balanced,
Performance, Battery Saver, Battery Balanced and Custom (see
[preset recipes](#preset-recipes)),
Persistent and UntilPowerSourceChange lifetimes, and explicit nullable clear.
Every save writes schema 10, as managed does. Each setter clears
the other performance slot as managed setters do. Temporary state saved on
a classified source expires when a fresh classified power source differs.
Unclassified external power expires only battery intent and never establishes
full-power effectiveness; a temporary saved while unclassified follows the
first classified source within the same Windows power generation (see below).
A status read expires nothing unless fresh Windows power agrees with its base.
Existing manual fan detail/custom/GPU fields
are preserved when unrelated; schema 1-7 requires managed migration. As in
managed, a non-false `controlsKeyboardEffect` below schema 9 or a non-null zone
control below schema 10 makes the document invalid. Stored fan speeds admit
managed's 5500 RPM maximum, while the fan write path keeps its 5400 RPM bound,
so a larger saved target is refused when selected. A fan request above 5400
RPM fails closed at decode: the native Service closes the connection without a
reply or error text. Managed decodes up to 5500 and refuses it at model
admission with a reply. Unsupported saved lighting overrides are preserved and
refused rather than erased or labeled as corrupt. Future, malformed,
duplicate-member and interrupted documents remain unchanged. A compatible
committed profile/preferences/commit envelope at schema 25, 26 or the current 27 is required when an aggregate exists. The same
legacy lighting rejections apply to it, and a schema-26 aggregate gets managed's
`PreserveLegacyIndependentProfileIntent` lighting normalisation on read. Hashes
and consumed typed thermal fields are validated before saving. This does not
implement all managed profile payload semantics or migrate/modify the aggregate;
schema 24 still needs managed migration first.

Without `service-profiles-v1` the installed Service does not serve
`updateProfiles`. The request gets managed's typed
`IPC request operation is unsupported.` failure (see
[unserved operations](windows-rust-development-service.md#managed-operations-the-installed-service-does-not-serve)),
and the aggregate stays exactly as managed wrote it. With that feature the
request is served as described under [Saved profiles](#saved-profiles-service-profiles-v1).

As in managed, every supported AC preset and the observed AC fan selector are
admitted on both classified external classes: an adapter at or above its
recommendation (PluggedIn) and a limited one (USB-C). The commands are the
same. Each setter needs a fresh adapter reading that agrees with Windows AC
presence (managed `AdmitOrdinaryThermalCommandAsync`). A class change between
setters continues only while the request's destination is the same for the new
class; battery, lost presence or a different destination stops the pair without
replay. A fan row can therefore journal
the Performance selector (2) with the limited source (2). Deployment readers
before `d6ce3339` reject such a row as invalid history, which fences
retirement, upgrade and mailbox custody fail-closed. The Rust `FanRecord` and
the managed `WindowsNativeFanHistory` reader both consume
`tools/openblade-windows-host/tests/data/fan-history-v3-limited-source.json`.
Battery presets are outside this writer.

### Preset recipes

Each admitted destination is one reviewed exact-RZ09-0581 0D/02 pair
`[1, channel, selector, 0]`, after managed `ProductionBatteryPort.SetPerformanceAsync`
(`src/OpenBlade.Protocol/ProductionBatteryPort.cs`) and the captured, applied,
read-back and restored entries of
`src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_BatteryProduction.json`:

| Preset (wire value) | Selector | Fixture actions |
| --- | --- | --- |
| Silent (0) | 5 | `performance-silent-channel-1/2` |
| Balanced (1) | 0 | `performance-balanced-channel-1/2` |
| Performance (2) | 2 | `performance-performance-channel-1/2` |
| Battery Saver (3) | 3 | `performance-battery-saver-channel-1/2` |
| Battery Balanced (4) | 6 | `performance-battery-balanced-channel-1/2` |

`InstalledThermalPreset` carries the wire value and selector; a protocol test
compares every encoded pair with the fixture. Each pair is written directly
from every confirmed state, manual fans included, with no Balanced or default
intermediate, and only the destination's own selector with Automatic fans
confirms it. Power admission follows managed `AdmitOrdinaryThermalCommandAsync`
(`ProductionBatteryPort.OrdinaryThermalPower.cs`) per destination
(`thermal_mutation::admits_class`):

- Silent, Balanced and Performance are ordinary commands. Every setter needs an
  external reading (full, limited or unclassified) that agrees with fresh
  Windows AC presence. Battery refuses them.
- Battery Saver and Battery Balanced (`IsBatterySelectorCommand`) are also
  admitted on battery, read with Windows battery, and on a classified adapter
  that agrees with Windows AC. Unclassified power refuses them. Managed skips
  the adapter read on Windows battery; native still reads 07/8C and needs it to
  read battery. The Windows battery reading must hold at every later
  checkpoint, as Windows AC must for an external reading.

A class change between setters follows the rules under
[unclassified external power](#unclassified-external-power); a change to or
from battery always stops the pair without replay. Saved-intent selection is
unchanged: battery selects only the battery presets
(`automation::performance_source_eligible`), so an override saved on AC is kept
and not written on battery.

On classified power, managed
`ValidateThermalOverrideCombination` checks a performance request before
anything is stored, and native does the same with the ported
`ProfileSetValidator.ValidatePerformanceFanCombination` and the exact RZ09-0581
policy profile saves use. The preset is the requested one (the power profile's
for a clear) and the fans are the saved fan override (the profile's without
one). A rejection is Failed with managed's text, for example:

- on battery, Silent, Balanced, Performance and Custom: "The requested intent was
  rejected: The performance preset is not valid for this power source."
- under a saved ManualCurve fan override, Battery Saver and Battery Balanced:
  "The requested intent was rejected: The performance and fan combination is not
  supported by the active validation policy." Fixed and Maximum fans admit them.

A class that disagrees with Windows is not managed's source. A battery reading
under Windows AC or unknown power is blocked before anything is stored, as
managed maps it to ExternalUnknown (`ResolveCurrentPowerSourceAsync`) and
`ExecuteScopedMutationAsync` blocks it: Blocked, "Hardware application was not
confirmed. The external power adapter could not be classified; overrides and
control writes are blocked." The reading does not expire temporary overrides.
Managed returns Battery whenever Windows reports battery, without sampling the
adapter, so the check runs against managed's source (`managed_power_source`).
A performance request on an external reading under Windows battery, or under
unknown Windows power, fails fresh admission first and stores nothing. A fan
request there is checked as Battery (see [native fans](windows-rust-native-fans.md)).

A fan request runs managed's `SetFanOverrideCoreAsync` checks on the same
classified power, before anything is stored: `FanControlPowerPolicy.IsAvailable`
for a requested mode ("The selected fan mode is unavailable on this model and
power source.", ManualCurve on battery), then the combination check with the
saved performance override, persistent or an unexpired temporary, or the
profile's preset, and the requested mode, or the profile's fans for a clear.
The saved override is not filtered by source here, as in managed: on battery a
saved Silent or AC preset rejects every fan request, a clear included. A
rejection is Failed with "The requested intent was rejected: " and managed's
text, and Stop still hands back an owned manual change.

Managed leaves confirmed HyperBoost for a battery preset indirectly
(`CanExitHyperBoostDirectlyTo`; the RZ09-0581
`HyperBoostBaseModeAdmissionSource` admits direct exits to Silent, Balanced,
Performance and Custom only). `ExecuteThermalSettersAsync`
(`ProductionBatteryPort.cs:2329-2442`) writes the `hyperboost-restore-balanced`
pair (`Rz09_0581_02e0_4_00_HyperBoostProduction.json:28-48`, the same payloads
as `performance-balanced-channel-1/2` at
`Rz09_0581_02e0_3_01_BatteryProduction.json:179-196`), then reads the full
thermal state and stops as indeterminate unless it reports Balanced with
Automatic fans, and only then writes the battery pair. After that exit the
battery pair skips the per-command ordinary source admission
(`ordinaryControl && !exitedHyperBoost`). So it is not the restore pair followed
by the battery pair: it has an intermediate confirmation and different per-command
power admission, and native has no recipe for it. Native stores the intent and
replies Blocked without preparing a row (`Plan::NoDirectPath`); this exit stays
deferred. A direct exit
to Silent keeps the first reading's classified external class, as the
Performance exit does.

### Custom

Custom (5) follows managed `SetCustomPerformanceAsync`
(`ProductionBatteryPort.cs:954-1003`): the selector-4 pair
`performance-custom-channel-1/2`, then the 0D/07 CPU `[1, 1, level]` and GPU
`[1, 2, level]` setters (`custom-performance-cpu-*` and
`custom-performance-gpu-*` in the same fixture), then the final 0D/82 pair read
and the 0D/87 CPU and GPU reads. A protocol test compares all nine level
payloads and both channels with the fixture. Only Custom with Automatic fans
and the requested levels confirms it. Any other final read leaves the row
pending without replay, where managed reports an indeterminate result; the
requested levels never become a confirmation. Custom is an ordinary command, so
battery refuses it, and it leaves HyperBoost directly with the first reading's
class pinned, as the Performance and Silent exits do. Each setter has its own
intent and acknowledgement row, so the row runs progress 0, 1-4, 7-10, then 5.
Prepared reserves that whole chain, the two-digit steps included. Under saved
manual fans on external power Custom defers, and the fan return to Automatic
writes the same four setters (see [Apply on fan handback](#apply-on-fan-handback)).

The admitted range is the RZ09-0581 capability
(`tests/data/profile-capabilities-rz09-0581.json`): CPU and GPU Low, Medium or
High except High/High, and no maximum fans
(`thermal_mutation::Destination::custom`). Request validation follows managed
`CustomPerformanceSettings.IsWellFormed`: CPU 0 to 3 and GPU 0 to 2 as canonical
integers, an optional `maximumFans` flag, and optional legacy
`gpuClockOffsets`, which exclude `gpuClockTuning`. A well-formed request
outside the range is stored and replies Blocked with the no-eligible-destination
cause; managed replies with its capability message instead. On unclassified
external power it is rejected before storage, as managed
`ValidateUnclassifiedPerformanceOverride` does. Non-null `gpuClockOffsets` has
no native writer and blocks with the GPU cause.

Saved and profile Custom use their stored levels. An application profile's
`customPerformance` replaces the power profile's, whichever of the two selected
Custom (managed `ApplicationProfileResolution`). A profile Custom without levels
selects Medium CPU and High GPU, managed's `CustomPerformanceSettings.Default`,
which managed's profile saves store in their place. Managed would try the
confirmed state's levels first; its normalized profiles never reach that, and
native does not consult it. A stored or requested override Custom always
carries its levels: the document reader and the request codec refuse one
without them.

`select` in the installed thermal runtime resolves the saved destination for a
fresh class without storing anything, which lets the
[request-driven reconciliation](#request-driven-reconciliation) of saved
profile intent reuse it.

Performance rows journal the destination's wire value, 0 to 5. The Rust
grammar and the managed `WindowsNativeThermalHistory` and `WindowsNativeHandback`
readers follow each destination's chain (`Progress::chain`): a preset is 0,
1-4, 5 and Custom 0, 1-4, 7-10, 5, and Prepared may retire as 6. A Custom
request carries its canonical `customPerformance`, which the intent digest
covers; a policy-selected row carries no request and no levels. A pending Custom
row hands back as Custom at progress 1-4 or 7-10. Both readers consume the
`presetRows` and `customRows` vectors in
`tools/openblade-windows-host/tests/data/thermal-history-v1.json`. Earlier
readers reject a Custom, Silent or battery row as invalid history, which fences
retirement, upgrade and mailbox custody fail-closed, as the earlier fan-history
extensions do. The row contract string is unchanged.

Status projects a persistent or temporary override as the desired destination
on battery and for Silent. Status metadata reports every external class as the
coarse class 3, where a battery preset is not eligible, so a Battery Saver or
Battery Balanced override on an adapter keeps the default desired slot in
status, while the saved override and the confirmed firmware selector are
reported. A clear keeps the default desired slot, as before. A Custom
destination also projects its requested levels as `desiredState.customPerformance`;
confirmed levels come only from a firmware read.

Status does not project the real class (0, 2 or 3). Its base carries only the
Windows power kind (`service_status.rs` `StatusSnapshot` construction,
`active_power_source`), and the 07/8C class is read only inside a mutation.
Caching that reading would go stale when one adapter replaces another without a
Windows power generation change, so status keeps the coarse encoding.

### Unclassified external power

Native ports managed's ordinary thermal control on unclassified external power
(`f6502f85`) for the setters it implements: Silent, Balanced and Performance,
and Fixed, Maximum and Automatic fans. Power is unclassified when Windows reports
AC online and the 07/8C query completed without a class: either a correlated
FAILED (3) or NOT_SUPPORTED (5) rejection with intact single-report geometry
(`RazerReport::is_completed_rejection`, after managed
`OrdinaryThermalPowerAdmission`), or a successful reply with the same
single-report geometry (`has_single_report_geometry`, managed
`WindowsRazerHidSession`) whose actual level is unmapped or whose recommended
level is unmapped or zero. Any other status, correlation, checksum, length or
geometry failure, and any receive failure, is no reading. The class is source
3; it is never promoted to full or limited power, and battery policy and
temporary full charge keep their classified-only admission.

The commands and the fresh 07/8C read before each setter are the same as on the
classified classes. An unclassified reading needs fresh Windows AC presence. A
performance setter needs it on every external class (`allowBattery: false`), and
so does a fan return to Automatic that carries a saved destination, which
managed applies through `SetPerformanceAsync` or `SetCustomPerformanceAsync`. Other fan setters on a classified class
need Windows AC that agrees with the reading, or Windows battery, where managed
admits them unchecked. The presence a reading
was admitted with must hold at every later checkpoint (managed
`AdmitOrdinaryThermalCommandAsync` and `OrdinarySourceUnchanged`), so losing it
stops before the next command. A class change between setters, either way
between full, limited and unclassified, continues while the new class selects
the same destination. A persistent explicit override does, and so does a
temporary saved unclassified within one Windows power generation. A temporary
saved on full or limited power does not, because the other class retires it,
and a profile destination does not, because unclassified power selects no
profile. Two transitions stay in their first reading's class: a direct
HyperBoost exit to Performance (after managed
`requireExternalPowerOnDirectHyperBoostExit`), and a fan transition that writes
the Performance state pair without carrying a saved preset, owned Stop included
(after managed acPreset with `EnsureThermalWritePowerAsync`). A target-only
change under manual fans has no such pin. Battery stops the pair without replay.
Without presence, an unclassified reading, like a confirmed HyperBoost state on
unclassified power (managed `CanAdmitUnclassifiedExternalControl`), is Blocked
before any intent is stored: `Hardware application was not confirmed. The external power
adapter could not be classified; overrides and control writes are blocked.`

No power profile or application profile applies there (managed
`CreateObservedFallbackProfile`). Explicit Silent, Balanced and Performance
overrides apply; a Battery Saver or Battery Balanced request, or one with GPU
clock tuning, is rejected before it is stored, Failed,
`The requested intent was rejected: This request needs classified power or
includes native overclocking; ordinary external presence does not admit it.`
A performance or fan clear replies Stored, `Override cleared. Power-profile
application waits for adapter classification.` A fan clear first hands an
OpenBlade-owned manual fan change back to Automatic under the observed
selector, without a saved preset (managed `RestoreOwnedAutomaticFansCoreAsync`);
if that handback is not confirmed, the reply is Blocked, `Fan control could
not be handed back to firmware. <cause>`. Owned Stop cleanup needs the same
presence and hands back on any external class, whichever class the owner's
row recorded: the commands are the same, and the rows keep the recorded
source. Status reports `activePowerSource` 3 and, while an explicit override
exists, that override as the desired destination.

A temporary override saved while unclassified records the Windows power
generation. A classified reading in the same generation keeps it and makes that
class its source, as managed `PreserveOverridesForExternalClassification` does;
a later different class expires it. The generation advances only when a fresh
sample changes the Windows ACLineStatus kind (managed `windowsPowerGeneration`).
Native samples the kind at every SCM power notice and whenever a request
resolves or checks its destination, so an unplug and replug between requests
expires the temporary, while a lock, an unlock, a suspend and resume or a
notice that keeps the kind do not. A failed sample, an unknown ACLineStatus
included, is not a boundary, and a change is seen only while a sample still
shows it: a replug that completes before the notice's sample is missed. The
status base reports 1 for battery and 3 for any other power, Unknown included,
and may be older than the read. Status reads saved intent at that class only
where fresh Windows power agrees with it, and otherwise at an unknown class,
which expires nothing (managed `CaptureControlPowerSource`: a failed read is
not a power boundary).

Fan rows can journal source 3. The Rust `FanRecord` and the managed
`WindowsNativeFanHistory` reader both admit it and both consume
`tools/openblade-windows-host/tests/data/fan-history-v3-unclassified-source.json`.
Readers before this change reject such a row as invalid history, which fences
retirement, upgrade and mailbox custody fail-closed. Performance rows carry no
power source, so their grammar is unchanged.

Native differs from managed here in these ways:

- Where no class pin applies, managed completes a pair whose class changes
  between setters whatever the new class's destination; native also requires
  that destination to be the same, so a profile request stops when the new
  class selects another one.
- Native pins a HyperBoost exit to the reading its destination was resolved
  on; managed pins it to the first command's own reading, taken just after.
- Native checks a pinned fan transition's class at the first setter too.
  Managed skips the pin on its first command, so a class change there gives
  managed one write and an Indeterminate result; native writes nothing, and an
  owned Stop leaves its 31 row pending for read-only reconciliation.
- Native requires the presence a reading was admitted with at every later
  checkpoint of the operation; managed requires it only until that command's
  own SET. A Performance entry admitted on Windows AC whose later commands see
  Windows battery completes its four writes in managed and stops in native.
- Managed admits a fan command with no reading check while Windows reports
  battery; native still reads 07/8C before each setter and stops on a battery
  reading, which no fan destination holds, and on an unclassified reading,
  which needs Windows AC. Managed's Balanced handback there writes both
  setters without an adapter query.
- On a Balanced HyperBoost exit native re-reads 07/8C before each setter and
  stops on a battery reading; managed reads power once for that exit.
- With manual fans saved and current, native replies with the Stored manual-fan
  deferral; managed has no deferral text there, and its coordinator leaves the
  manual fans in place.
- Managed samples the Windows power kind at every control capture; native
  samples it at SCM power notices and when a request resolves or checks its
  destination, so it sees a kind change only while one of those samples still
  shows it. Native does not track managed's separate last classified class per
  generation.
- Native status carries no `externalPowerPresent` or capabilities, and with no
  override its desired provenance stays the profile default; managed reports
  the confirmed firmware state.
- Managed also admits lighting and battery controls on unclassified power;
  those are not native thermal setters and are unchanged.

Resolution uses eligible manual intent, then the current application report's
performance override, then the matching power profile. Application instances have
monotonic sequence numbers and a 15-second lease, use full canonical executable
paths and production Windows ordinal comparison, and cannot supply caller SID or
hardware authority. With `service-profile-triggers-v1`, a report that changes
the active pair applies the saved intent (see
[Request-driven reconciliation](#request-driven-reconciliation)).
It can use the existing managed reporter wire operation;
complete native Session Agent producer parity is separate work. Private profile
and accepted override projection carries a local authenticated SID binding. Final
GetStatus, ObserveState and subscription projection rechecks the live peer;
System, inactive and different-SID callers receive only public hardware fields.
Each projection re-resolves private intent against current public metadata.
Stored-only publication merges onto the latest hardware without renewing a read
witness or timestamp. Late accepted persistence becomes visible on a later fresh
read without replaying a write.

Configured GPU clock effects are saved without a thermal pair and reply Blocked
with managed's committed-intent prefix, `Performance override intent was stored.
Hardware result: GPU clock handling is incomplete...`. GPU clocks, manual fan
application, display, lighting, and other native controls are not implemented
here. Their saved fields are not confirmation. The broad capabilities snapshot
remains null. Applied means this bounded thermal preset was independently
confirmed, with GPU/broad capability incompleteness stated in the result. An
ineligible saved request also replies Blocked with that prefix, even when the
policy-selected preset is confirmed; the hardware result then says that the
policy selects another destination. Requested override, effective
selected value/provenance, and observed state remain distinct. Mutation success
frames contain only the operation result, as required by the unchanged v85
single-body response contract. Status travels separately through GetStatus,
ObserveState and subscriptions.

### Reply parity with managed

Native replies follow managed's status and message shape for saved but
unapplied intent:

| Native case | Reply |
| --- | --- |
| External power (PluggedIn, limited USB-C or unclassified) with current-manual plus desired-manual fans, when a native destination resolves | Stored, `Performance intent was stored and will be applied when fans return to Automatic.` |
| The same manual-fan state with no native destination (for example a clear under a High/High Custom profile) | Blocked, `Performance override was cleared. Hardware result: No supported performance destination...` |
| Confirmed HyperBoost and a Battery Saver or Battery Balanced destination | Blocked, `Performance override intent was stored. Hardware result: Leaving HyperBoost for a battery preset needs the HyperBoost restore sequence, which this build does not submit; no thermal setter was submitted.` |
| OpenBlade-owned fan row whose handback to Automatic is outstanding, without that manual deferral | Blocked, `Performance override intent was stored. Hardware result: OpenBlade still owns a fan change...` |
| GPU effect, ineligible policy, power change, admission, capacity, storage or transport cause after the intent was saved | Blocked, `Performance override intent was stored. Hardware result: <cause>` |
| Saved clear that was not applied | Blocked, `Performance override was cleared. Hardware result: <cause>` |
| Fan intent saved but not applied | Blocked, `Fan override intent was stored. Hardware result: ...` or `Fan override was cleared. Hardware result: ...` |
| Performance or fan clear on unclassified external power, with no owned fan change left | Stored, `Override cleared. Power-profile application waits for adapter classification.` |
| Fan clear on unclassified external power whose owned handback is not confirmed | Blocked, `Fan control could not be handed back to firmware. <cause>` |
| GPU clock tuning, Battery Saver or Battery Balanced requested on unclassified external power | Failed, `The requested intent was rejected: This request needs classified power or includes native overclocking; ordinary external presence does not admit it.` |
| Unclassified 07/8C reading without fresh Windows AC presence, or with confirmed HyperBoost; or a battery reading under Windows AC or unknown power | Blocked, `Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked.` |
| Fan mode the power source does not admit, or a saved override and fan mode managed's combination check rejects | Failed, `The requested intent was rejected: <managed text>` |

The Stored text is managed's `DeferredPerformanceIntentMessage`. Native
returns it only when the later fan request can apply the intent, so the
no-destination check runs before the manual-fan deferral. The fan request
writes every native destination, as managed's fan return does. A build
without `service-fan-mutation-v2` has no native fan request, so it keeps the
older instruction to submit a new performance request after selecting
Automatic fans.

### Apply on fan handback

Managed applies the saved performance intent when a fan request returns
manual fans to Automatic. On exact RZ09-0581 (direct manual transitions, no
performance report under manual fans),
`ControlCoordinator.ApplyDirectAutomaticFanTransitionOutcomeAsync` writes the
resolved destination through
`ApplyPerformancePreservingGpuClockManagementOutcomeAsync`: a preset's own
0D/02 pair, which carries the Automatic fan mode, or for Custom
`SetCustomPerformanceAsync`'s selector-4 pair and its 0D/07 CPU and GPU
setters. It does this when the saved fan intent before the request was manual
(`previousManual`), and replies `Fans returned to Automatic with <preset>
performance.`

Native does the same in the fan request. When a `setFanOverride` clear or
Automatic request leaves saved manual fan intent, the fan worker resolves the
destination as a performance request would (manual override, application
report, then the profile for the freshly read power class) and returns the fans
through that destination's setters instead of the observed selector: Balanced,
Performance, Silent, Battery Saver or Battery Balanced as one pair, or Custom
as its pair and both level setters (see [Custom](#custom)). The setters have
the same codec and bytes as the performance writer's; there is no fan pair
first, no reset and no 210 ms delay (`applyMaximumTiming: false`). The existing fan-path admission applies to each
setter: fresh exact-device, caller, session, controller and lifecycle admission,
a fresh 07/8C read that agrees with Windows AC presence, and unchanged saved fan
settings and destination preset, also for a new class between setters. An
independent final 0D/82 pair must confirm
the destination with Automatic fans, and for Custom the 0D/87 CPU and GPU reads
must report the requested levels; other or unread levels leave the row
unsettled. The reply is `Fans returned to Automatic with
<preset> performance, confirmed by independent paired readback.`

This holds for manual fans under every decoded selector. Managed maps Silent,
Battery Saver, Battery Balanced and Custom with manual fans to
`FanMode.Maximum` (`ProductionBatteryPort.MapThermalState`), and
`CanApplyAutomaticPerformanceFrom` and the Custom setter admit every
destination from `FanMode.Maximum` (`ProductionBatteryPort.cs:810-822` and
`:988-991`). The destination's own pairs are the fixture's Automatic pairs
under each selector (`Rz09_0581_02e0_3_01_BatteryProduction.json`, the
`performance-*-channel-1/2` entries). Without a destination
(`Plan::select_toward`) native has no plan for those baselines and writes
nothing: its fan state pair admits selectors 0 and 2 only (`FanPair::state`).
Managed does hand them back there. Its direct transition
(`ProductionBatteryPort.cs:1278-1323`) rewrites the fan state pair under the
observed selector with fan value 0, which is the same bytes as that selector's
performance pair in the fixture. That handback is deferred, not ported. With a
GPU clock effect configured, a return to Automatic carries no destination and
a performance request under manual fans defers to the fan return, so native
leaves such manual fans by neither request.

A saved Custom outside the RZ09-0581 range has no destination. Managed applies it
anyway, and its capability check (`ApplyScopedAsync`, `Unsupported`) refuses it
before any write, so the fans stay manual. Native does the same: the fan intent
is saved, nothing is prepared or written, the owner stays outstanding and the
reply is Blocked, `Fan override intent was stored. Hardware result: Fans could
not return to Automatic with the saved Custom performance intent. Performance
value is not supported by the verified capability table.` (`Fan override was
cleared.` for a clear).

The fan journal row records the destination selector. Manual fans keep
selector 0 or 2. An Automatic row may also carry Silent (5), Battery Saver (3)
or Battery Balanced (6), the battery pair never on unclassified power (3), or
Custom (4) with its levels in `custom` (`[cpu, gpu]`, the RZ09-0581 range,
High/High excluded). `custom` is absent otherwise, so earlier rows keep their
bytes and digest, and an explicit null is refused. A Custom row reserves its
four setters, two power readings, the final pair and the level reads. Rust
`FanRecord` and managed `WindowsNativeFanHistory` share
`tools/openblade-windows-host/tests/data/fan-history-v3-destination-return.json`;
readers before this grammar refuse these rows, which fences retirement,
upgrade and mailbox custody. Progress follows the fan chain (Prepared, 10 to
13, 50, or 10 to 17 for Custom). A power change before the first setter settles
as KnownNotSubmitted (6). A later stop, cancellation, rejected echo or readback
mismatch leaves the row unsettled and fences further writes; nothing is
replayed. A native-to-managed handback of such a row names the fan family with
Automatic only. Managed then confirms only whether the fans are manual
(`WriteConfirmationPolicy`) and, recovering from the native run, applies no
profile (`ServiceStartupCoordinator`), so the saved preset stays unapplied until
a later explicit request.

The fans keep the observed selector, and the saved intent stays unapplied, when
the saved fan intent was already Automatic, or when a GPU clock effect is
saved; the reply then says
the GPU effect left it unapplied. The GPU check uses the same class expiry as
the destination, so a temporary override kept for another class does not count.
Managed's trigger is the request's saved fan intent, not an outstanding apply:
after a return refused before any setter (its Automatic intent is already
saved), a retry keeps the observed selector. When an Automatic request confirms
a selector other than the saved preset, whichever native destination it is, the reply adds
`The saved <preset> performance intent was not applied: submit a new
performance request to apply it.` Owned Stop cleanup writes, when it must,
only through the owner's selector: shutdown reaches the firmware handoff
without applying saved settings. When fans already read Automatic, under any
selector, Custom or HyperBoost, Stop writes nothing and closes the owner with
51 (see [native fans](windows-rust-native-fans.md); managed instead rejects a
Custom High/High read and keeps ownership). Native has no automatic
reconciliation, so fans returned to Automatic by firmware or another
controller do not apply it.

Native replies Failed only for GPU clock tuning on unclassified external
power, which it rejects before storing, as managed does (see the reply table).
These other managed Failed cases have no unambiguous native counterpart, so
native keeps Blocked:

- Uncertain submission by this request. Native leaves the journal unsettled and
  replies Blocked with the reconciliation reason. Managed replies Failed, `<Name>
  write is indeterminate...`, but an unsettled journal also follows a checkpoint
  or ownership stop after the first intent row, which managed reports as
  Blocked. (A stop after Prepared only settles as known-not-submitted.)
- Lease transport error after the intent was saved. Native replies Blocked with
  `Thermal transport or ownership did not complete`. Managed replies Failed for
  read failures, rejected writes and indeterminate writes, and Blocked for
  capability or ownership refusal; the native lease error carries both classes.
- Read or readback failure inside the worker, including a changed final
  readback. The worker returns no observation without an error class, so native
  replies Blocked with the admission or reconciliation reason. Managed replies
  Failed, `could not be read before writing` or `changed before the final
  aggregate readback`.
- Other intent rejected with nothing saved. Native replies Blocked, `Performance
  intent was not accepted...` or `Saved configuration replacement failed...`.
  Managed replies Failed, `The requested intent was rejected: ...`, except
  Blocked for a quarantined configuration. Native saves after the hardware
  observation and uses one refusal path for quarantine, an unsettled journal,
  validation and file errors.
- The same classes in fan replies, which are Blocked whenever the fan intent is
  not applied.

Mapping any of these needs native error classification first; a status change
alone would misreport the other class.

## Saved profiles (`service-profiles-v1`)

`service-profiles-v1` adds `updateProfiles` on top of the temporary-charge
lane. It is a candidate source composition like the others: not selected,
installed or published, and exact RZ09-0581 only. Saving profiles writes no
mailbox command.

The typed model, codec and validator are in
`rust/crates/openblade-domain/src/profile_set` (feature `profile-set`):

- `model`, `decode`, `encode`, `json`: the whole managed `ProfileSet`, bound
  and written by `System.Text.Json` rules. The reader keeps member order and
  number text; the writer reproduces `ProfileStore`'s bytes (CRLF indentation,
  default encoder escapes, shortest round-trip doubles, the computed
  `isConfigured` and `effectiveOffsets` members).
- `normalize`, `lighting`, `resolve`: `ProfileStore` normalisation, lighting
  well-formedness and application-override resolution.
- `policy`, `validate`: `ProfileValidationPolicy` and `ProfileSetValidator`
  with managed's messages. The policy is built from
  `Capabilities::rz09_0581()`, the capabilities the managed battery and
  lighting factories admit for that model.
- `document`, `preferences`: `ProfileStore.ReadDocument` and
  `BatteryConfigurationStore.ReadPreferences` for schemas 1 to 27, and the
  documents managed writes back.
- `migrate`: the rewrite `BatteryConfigurationStore.LoadCoreAsync` makes when
  the store opens, for a committed aggregate and for the schema-less legacy
  profile. See [Migration when the store opens](#migration-when-the-store-opens).
- `update`: the intent half of `UpdateProfilesAsync`.

A request is handled in managed's order:

1. The frame is decoded and admitted as `IpcMessageCodec` does. A frame it
   refuses closes the connection with no reply.
2. The set is normalised, validated as stored intent, and its changed thermal
   intent is validated against the device. A refusal is managed's `Failed`
   reply, `The requested intent was rejected: <message>`, and nothing is
   written.
3. The intended documents are read with the reader this service uses on
   every later request and when it starts. A set that reader would refuse is
   answered `Failed` and nothing is written, so the service never stores what
   it cannot read back.
4. The aggregate is replaced in one recorded transaction: an exact backup of
   each document, then `battery-configuration.commit.json.recovery-required`,
   then profile, preferences and marker, then managed's verification: the
   store holds exactly the intended bytes and they read back as the admitted
   set. The preferences bytes are carried over unless reading them migrated
   something. The record and backups are removed only after the verification.
5. The reply is `Stored` with
   `Profiles stored without automatic hardware application.`, and the saved
   set is published to the owner at the next status revision. A save that
   finished after the frame deadline is answered all the same.

If a replacement or the verification fails, the exact prior documents are
restored and the reply is `Failed`. If the restore or the record removal
cannot be confirmed, the record and backups stay, the store is quarantined,
and later requests are `Blocked`. As in managed, nothing recovers a recorded
transaction after a crash; the record preserves the aggregate until it is
reviewed.

A staging file is different from the record: it is never evidence. The
store's replace writes `<name>.pending` and renames it over the document, and
leaves that file where the rename fails. The transaction takes the stage of a
failed write back itself. Before it starts, and only when no record exists, it
also removes any stage left under a name it is about to write: the three
documents, the record and its backups. A crash between creating the record's
stage and renaming it therefore blocks nothing, as in managed, whose stages
carry the transaction identifier and are only ever deleted. If a leftover
stage cannot be removed, the reply is `Failed` and says so, nothing is
written, and the next request tries again. The store's replace itself is
unchanged: a failed `manual-overrides.json.pending` and a pending journal stay
on disk as the evidence the handback to managed preserves, and the installed
store refuses to discard either.

Managed is the reference, and five shared vector files under
`tools/openblade-windows-host/tests/data` pin it:
`profile-set-vectors-v27.json` (decode, validation, reply and stored bytes),
`profile-aggregate-vectors.json` (loading and migrating schemas 1 to 27),
`profile-migration-vectors.json` (what opening the store leaves on disk),
`profile-ordinal-ignore-case.json` (the comparison that finds duplicates) and
`profile-capabilities-rz09-0581.json`. The managed tests
`ServiceControlRuntimeTests.NativeProfile*` record every expectation from the
IPC codec, the production validator, `UpdateProfilesAsync` and
`BatteryConfigurationStore.LoadAsync`, and verify the same files the Rust
tests read. To regenerate after a managed change, set
`OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS` to that data directory and run
those tests.

Differences from managed:

- Managed restores lighting it had suppressed after a save; native does not.
  With `service-profile-triggers-v1`, an edit that changes the active
  application profile's thermal intent applies the saved performance and fan
  intent, as described in
  [Request-driven reconciliation](#request-driven-reconciliation); battery
  and lighting are not applied.
- Member names are matched exactly. Managed's IPC reader ignores letter case;
  the Tray writes camelCase.
- Duplicate application executable paths and THX rule names are found by
  `text::same_ordinal_ignoring_case`, which is managed's `OrdinalIgnoreCase`
  as a table: one character against one, never an expansion (a sharp s is not
  `SS`) and never an ASCII letter for a character outside ASCII (a dotless i
  is not `I`). A managed test records every set of characters the comparer
  holds equal in `profile-ordinal-ignore-case.json`, and the Rust test checks
  every character against it. The table is managed's as of the .NET runtime
  that recorded it. When a newer runtime joins more letters the managed test
  fails; record the vector again and run the domain vector tests with the
  same variable set to rewrite `profile_set/ordinal_case.rs`.
- The saved aggregate's reader and the match of the foreground application
  against a saved profile still use the Windows ordinal comparison, as the
  earlier lanes do. It keeps apart some pairs managed joins (final sigma, the
  micro sign, title-case digraphs, Cherokee, Georgian and letters outside the
  basic plane), so an application profile saved under one of those spellings
  is not matched by the other. A set is refused before it is written if that
  reader would not accept it.
- Two `Failed` replies have no managed counterpart: the set this service's
  reader would refuse, and the leftover staging file that cannot be removed.
- A request that waits longer than the frame timeout behind another mutation
  is closed without storing anything. Managed waits.
- An application override that selects fan Stop without fan settings closes
  the connection. Managed throws a `NativeComponentException` there and sends
  no reply either.
- A keyboard sequence is checked against the six by seventeen grid only.
  Managed also knows three layouts with their own key maps; a set naming one
  is refused as malformed lighting rather than as a different layout.
- Executable paths follow `Path.GetFullPath` as the vectors record it: a
  `\\?\` or `\??\` path is stored as written, a `\\.\` device path keeps
  its prefix and is otherwise normalised like any other. Two cases depend on
  the machine and are not modelled. A path with an 8.3 short name that exists
  on disk is accepted; managed expands it and refuses the path as not
  normalised. A reserved device name such as `NUL.exe` is accepted, as on the
  Windows 11 build that recorded the vectors; an older Windows maps it to
  `\\.\NUL` and managed refuses it there.
- Battery Saver fan stop cannot be newly enabled, because this composition has
  no Fan Stop. Managed allows it once Fan Stop is admitted at run time.
- A request from another session, or while a thermal submission needs
  reconciliation, is refused as every installed mutation is (closed
  connection, or `Blocked` with the reconciliation reason). Managed stores
  profiles in both cases after its own session check.
- Each document is staged as `<name>.pending` by the store's replace
  primitive instead of a `.stage` file, the transaction identifier is the
  digest of the change instead of a random GUID, and the record lists file
  names instead of full paths. Only the record's presence is ever read. The
  fixed stage name is why a leftover has to be removed before the next save.
- A failed transaction is reported in native words. Managed's text comes from
  the .NET I/O exception.
- The published status carries no `Profiles stored...` message.

An older aggregate is migrated when the store opens, as described next. An
absent aggregate is still materialised from the safe defaults by the first
save.

### Migration when the store opens

Managed migrates when it loads the configuration, not at the first save.
`BatteryConfigurationStore.LoadCoreAsync` (`BatteryConfigurationStore.cs:349-417`)
writes the aggregate back through `SaveConfigurationTransactionAsync` whenever
`ReadCommittedSnapshot` reports a migration, and always for a legacy candidate.
Native does the same when `Storage::open_with` runs. Called once from
`thermal_storage.rs`, `profile_storage::plan_migration` decides, reading only
the configuration documents and the record, whether the open writes a
migration, and `Planned::write` writes it. Only `service-profiles-v1` has the
call. The other lanes still refuse schema 24 and older and the legacy profile,
and write no profile document.

The open runs its pending checks first: pending override or journal evidence
and a recovery-required record. An open that writes no migration, because the
aggregate is current, absent, refused, or one this service could not read back
once migrated, keeps the order it had before migration existed: the aggregate,
then the override document, then the journal. A refused aggregate is the
answer, and the journal is not read.
`a_refused_aggregate_is_answered_before_the_override_or_the_journal` pins that
on every lane, and
`an_aggregate_no_migration_writes_is_refused_before_the_override_or_journal`
pins it for an aggregate managed would migrate but native refuses.

An open that writes a migration admits the override document and the journal
first, then writes the migration, then admits the migrated aggregate. When
either of the first two is refused, the configuration documents are not
written. Before its first backup, `Planned::write` reads the three documents
again; if another writer changed them after the plan, the open fails with
`The saved configuration could not be migrated to the current schema. It is
unchanged.` and writes nothing, so the transaction never backs up or restores
planned bytes over newer ones, and the next open plans from what is stored
then (`documents_another_writer_changed_after_the_plan_are_left_unchanged`).
A change after that read falls to the transaction's own checks, as a save's
does. That open is not read-only: its journal read is the one every open
makes, and the journal store's read authenticates the rows, can fail fatally on
the owner binding and can publish a pending roll. Managed's `ReadContextAsync`
loads the battery store first and its overrides after, and quarantines an
override document on its own; native refuses that whole open, so migrating
first would only rewrite an aggregate the service still could not use. The
record check also makes the record guard inside `plan_migration` unreachable
from this call site, which holds only while the call stays after those checks;
`pending_override_or_journal_evidence_keeps_an_older_aggregate_untouched` and
`an_override_or_journal_the_open_refuses_keeps_an_older_aggregate_untouched`
pin the placement.

Two sources migrate, through `profile_set::migrate`:

- A committed aggregate at schemas 1 to 27 that `read_aggregate` reports as
  migrated: every schema below 27, and a schema 27 aggregate that
  normalisation changes. This is managed's chain. `ProfileStore.ReadDocument`
  (`ProfileStore.cs:182-310`) rejects lighting members newer than the document
  (`:205-216`) and normalises the battery profile
  (`NormalizeLegacyBatteryProfile`, `:384-437`), taking the protection default
  from the preferences below schema 11 (`:228-234`). It clears Windows power
  modes below 12, migrates the per-profile modes of 12 to 14 and clears any
  left at 15 and later (`:235-252`, `:492-553`). It clears application
  profiles and display brightness below 22 (`:253-256`, `:359-372`), holds
  schemas through 17 to the 50 to 80 percent grid (`:291-294`, `:374-382`),
  clears Battery Saver fan stop below 23 (`:295-303`) and keeps schema 26's
  independent intent (`:304-307`). `BatteryConfigurationStore.ReadPreferences`
  (`BatteryConfigurationStore.cs:573-686`) drops the protection setting, sets
  Maintain desired state with the disclosure pending below 16, defaults Fan
  Stop below 24, derives its cutoffs at 24, and re-enables experimental
  features.
- The schema-less legacy profile: `profiles.json` alone, with exactly
  `pluggedIn`, `battery` and `batteryPolicy` (`ReadLegacy`, `:525-571`). A
  power profile has exactly `performance`, `fans` and `lighting`; lighting has
  exactly `effect`, `color` and `brightness`; a color has any of `red`,
  `green` and `blue`, and an absent one is 0. Every value is a JSON number
  that fits a 32-bit integer, and a color or brightness also fits a byte, so
  `-0` is zero and `"80"`, `80.0` and `8e1` are refused. `temporaryFullCharge`
  must be `false`. The limit must be 50 to 99 for `BatteryPolicy` and then 50
  to 80 in steps of 5. USB-C takes the safe defaults and battery protection
  defaults to on. The preferences are the defaults with Maintain desired state
  and the upgrade disclosure pending (`:354-362`). A lone profile that declares
  a schema is incomplete, or future above 27, as managed reports it.

The documents are the bytes `SaveConfigurationTransactionAsync` writes
(`:696-702`), and they go through the save's recorded transaction above: an
exact backup of each existing document, the record, profile, preferences and
marker, verification, then removal of the record and backups. The originals
stay on disk until the migrated bytes are verified. Managed leaves only the
three documents behind, and so does native; managed's backups and stages carry
a random transaction identifier, native's the change digest and `.pending`.

Everything else is left exactly as stored for the reader to admit or refuse: an
aggregate managed preserves (a future schema in any file, a missing file, a
marker of other bytes, a mixed or an invalid document), a recorded
transaction, an absent aggregate, and one this service's reader would refuse
once migrated.

A crash at each durable step leaves:

- In a backup's stage or backup, or the record's stage: the documents
  untouched and no record. The next open removes the stage, reuses an exact
  backup and migrates.
- After the record lands, through every document replacement and the marker
  (the commit), until the record is removed: the record, exact backups, and
  each document holding its prior or its migrated bytes. The store stays
  closed and nothing is touched, as managed's `CheckRecoveryRequired` does.
- After the record is removed: the migrated documents and possibly some exact
  backups, which nothing reads. The store opens. Nothing collects those
  backups later, as managed's are never collected either.

A failure that does not stop the process restores the prior documents, and
the open fails:

- A migration the store read back as other bytes is managed's verification
  failure. `VerifyIntendedAggregateAsync` throws `InvalidDataException`
  (`BatteryConfigurationStore.cs:914-932`), which `IsRecoverable` admits
  (`:1344-1349`), so `LoadCoreAsync` quarantines the aggregate (`:403-438`).
  Managed keeps the quarantine while that user's context stays active:
  `ActivateUserAsync` keeps an active same-user context and loads the
  configuration again when the user activates after another user
  (`ServiceControlRuntime.cs:1456-1487`). Native refuses the open with
  managed's quarantine text, `OpenBlade preserved an unsupported or invalid
  configuration. ...`, and holds one entry on the service runtime's thread:
  the user's SID and the change, the digest of the stored and migrated bytes.
  A later open of that user's store with the same bytes is refused the same
  way without a write. The hold ends when another user's open succeeds, as
  managed loads the held user's configuration again only after another user
  activates, or when the process exits. Another user's refused open leaves it,
  and a successful one forgets it whether or not it migrated anything
  (`only_another_users_successful_open_forgets_a_hold`). Because
  the change is keyed by content, other bytes in the same store are another
  change, which is attempted and takes the hold's place, and another user's
  byte-identical store is attempted too. The runtime serves one user at a
  time, so one entry needs no cap. `ProtectedFiles::replace` reads back each
  file it writes and fails on a disagreement (`thermal_native.rs:418-420`),
  so on real storage the hold engages only when the transaction's own
  verification then reads other bytes, as when another writer changes a
  document between them.
  `a_migration_another_writer_changed_is_restored_and_held_on_real_files`
  pins that on real files.
- Any other failure, an I/O error before or after the record, fails the open
  with `The saved configuration could not be migrated to the current schema.
  It is unchanged.` Managed's load throws its I/O exception there, and the
  next request tries again: `ControlRequestHandler` activates the user for
  every interactive request (`ControlRequestHandler.cs:211-241`).
- If the restore cannot be confirmed, the record stays: `Configuration
  transaction recovery is required. ...`, and every later open is refused.

Schemas 25 and 26 used to open without a write. Now a migration of them, or of
any older aggregate, that cannot be written fails the open, and every thermal,
fan, battery and profile request answers from that failed open. A failed open
is not kept, so each request opens again and retries an I/O failure, as
managed's next request retries its load. One attempt makes up to 14 durable
writes and leaves the prior aggregate. A stage that cannot be removed, or
another file under one of the change's backup names, fails every attempt the
same way until it is removed. The only failure managed holds, the
verification failure, is held as described above.

`profile-migration-vectors.json` holds 120 cases recorded from
`BatteryConfigurationStore.LoadAsync` by
`ServiceControlRuntimeTests.NativeProfileMigrationVectors`: every schema from 1
to 26, schema 27 left alone and schema 27 rewritten, boundary values, 54
legacy profiles and the envelopes managed preserves, each with the files left
on disk and their digests. Ten rows carry `nativeDifference`: managed's
outcome is still their expectation, and the native readers pin their own
refusal with the files untouched. The domain test `profile_migration_vectors`
migrates every case the payload reader sees. The host tests in `profile_migration_tests.rs` open the
store on every case and compare the files, fail and crash each write, and
repeat the open; four real-file tests run through `ProtectedFiles` with the
injected verifier. Both readers refuse a malformed or unknown row.

Differences from managed:

- An absent aggregate is not materialised at open; the first save does it.
- A migration that `thermal_storage::aggregate` would refuse once written is
  not written, so a schema 24 or older aggregate in that state keeps the store
  closed where managed migrates it. Duplicate application paths are compared
  as managed compares them, so this needs another disagreement between that
  reader and `read_aggregate`.
- A document with a duplicate member that also declares a future schema is
  refused as invalid by the domain reader; managed names it a future schema.
  Both leave the files untouched and the store closed.
- A lone profile with a duplicate member and a schema version is incomplete
  to managed and invalid to `profile_set::migrate`. The host's inspection
  names it as managed does, and both leave it untouched. Pinned by the
  `legacy duplicate member with a schema version` row.
- Some nulls managed reads and then discards or migrates are refused, with the
  files untouched and the store closed:

  | Shape | Schemas |
  |---|---|
  | `applicationProfiles: null` | 2 to 14 |
  | `[null]`, or an entry whose `overrides` or `executablePath` is `null` | 2 to 21 |
  | an entry whose `name` is `null` | 2 to 21 |
  | `windowsPowerModes: null` | 2 to 14 |

  Managed discards application profiles below schema 22 and migrates or
  discards Windows power modes below 15 (`ProfileStore.cs:235-256`,
  `:492-553`), then migrates the aggregate; the native decoder refuses the
  null first (`profile_set/decode.rs:698-705`). From schema 15 managed
  quarantines a null list and null power modes, and from 22 a null entry, as
  native does. Nine `nativeDifference` rows pin the shapes, and ordinary rows
  pin the schema 15 and 22 boundaries.
- The store's own reader (`thermal_storage::aggregate`) refuses a current
  aggregate whose application profile omits `enabled`; managed defaults it
  to `true`. This predates the migration and is unchanged here.

Packaging refuses this composition. `build.rs` gives a build with
`service-profiles-v1` the temporary-charge capabilities followed by
`service-profiles-v1`, and both package readers (`native_package.rs`,
`WindowsNativePackage.RequireCapabilities`) refuse that feature and that
capability by name, whatever else a record claims. A profile-serving image
cannot be packaged, bound or installed, as the temporary-charge composition it
extends or as anything else. Tests on both sides pin the refusal. Admitting it belongs to the packaging stage.

### Active application profile

Resolution of the active application profile is ported. The installed Service
resolves every request's profile layer through it (see below). With
`service-profile-triggers-v1`, a changed foreground report, a report while the
session owes a reconciliation and a save that changes the active
application's thermal intent apply the saved intent
([Request-driven reconciliation](#request-driven-reconciliation)); lease expiry
and power changes do not trigger one yet.
`openblade_domain::application_context` holds managed `ProfileAutomationState`'s
selection rules over borrowed rules: the first enabled rule whose path matches
a report (`SetApplicationContext`, `ProfileAutomationState.cs:216-236`), the
update that keeps the active pair only while an enabled rule with its id still
holds the reported path (`UpdateProfiles`, `:130-143`), the rule whose overrides
apply (`GetApplicationOverride`, `:632-641`) and the save path's
`activeApplicationIntentChanged` predicate (`ServiceControlRuntime.cs:2116-2119`).
`profile_set::application` binds them to the typed set with managed's
`OrdinalIgnoreCase` table.

`resolve` takes both power sources managed reads and the confirmed firmware
state `Resolve` reads. The state source, `ProfileAutomationState.PowerSource`,
selects the override, `EffectiveLaptopBehavior` (`:99-110`) and
`DesiredBattery`'s protection (`:84-89`). `Resolve` starts from
`observedPowerSource ?? PowerSource` (`:322-344`), and `ResolveAutomationDesired`
passes `ExternalUnknown` whenever `ProjectUnclassifiedIntent` holds
(`ServiceControlRuntime.cs:19483-19490`,
`ServiceControlRuntime.UnclassifiedExternalPower.cs:151-152`), even while an
explicit unclassified override leaves the state source classified. So the
resolved profile follows the projected source and the behaviour follows the
state source. On `ExternalUnknown` that profile is `CreateObservedFallbackProfile`
(`:554-561`) with no override: confirmed performance, fans and lighting, each
falling back to the saved USB-C value, the confirmed Custom settings only, and
`DesiredBattery`'s protection. A USB-C edit therefore changes the desired state
on unclassified power wherever the matching confirmed member is unknown, and
`UpdateProfilesAsync` acts on that change: it skips the lighting restore
(`ServiceControlRuntime.cs:2121-2122`) and pauses an armed fan stop (`:2154`).

`desired_intent` is the profile layer of `Resolve` for every
`DesiredDeviceState` member, with each setting's source. It is the predicate's
desired term only while no manual override, recovered HyperBoost intent or
temporary battery override is active. A save that changes only display, THX
audio or the gaming-mode key policy is no change; removing an override equal
to its base is one.

An active override can hide a profile change or add one. A persistent Silent
override over an edited rule preset leaves managed's desired state unchanged
while the profile layer changes. A persistent Custom override without levels
takes the profile's Custom settings even when the profile preset is not Custom
(`:391-398`), and the legacy GPU offset move runs only after those layers are
chosen (`:400-415`). The wiring slice composes the full term from the profile
layer. It layers performance, fans, fan control, lighting and battery over each
member's `Setting::source`. It takes Custom settings and GPU tuning from the raw
`Resolution::profile`, not from `DesiredIntent`, layers the overrides over them
and then reapplies the move with `gpu_clock_settings`. When neither an override
nor the profile carries Custom levels, the confirmed levels and then managed's
default follow, as `DesiredIntent::custom_performance` does (`:391-398`). It puts
fan control into an override's mode with `with_mode`.

The replay's `application_runtime::Lease` uses the same selection rules, and
`Lease::rule` also requires the pair to be retained by the current rules, so a
caller that changes rules without `Lease::update` gets no overrides.

The installed Service composes over the same resolution.
`thermal_mutation::Runtime` keeps the latest admitted report with its lease as
a `thermal_storage::Foreground`, the active pair the report selected
(`Storage::select_application`, `ApplicationContext::report`, under the
mutation gate). A save retires the pair through `ApplicationContext::update`
(`Storage::retain_application`) and never selects. Each request step reads the
aggregate once into a typed `ProfileSet` (`Storage::profile_layer`), uses the
pair only while the saved rules still retain it
(`ApplicationContext::retained`), and resolves the step's power class with
`application::resolve` (`ProfileLayer::at`). `Storage::desired`,
`gpu_incomplete`, `custom_unsupported`, `desired_fan` and
`desired_manual_fans` take that one `Resolution` and layer the overrides over
it: a persistent performance override (source 4), then a temporary one (3),
then the resolved profile, sourced from the application (2) when its override
sets the preset, the Custom levels or the GPU tuning, as managed's
`DesiredDeviceState.Performance` does, and from the power profile (1)
otherwise. Fan intent
is an unexpired temporary fan override, then a persistent one, then the
resolved profile's fan control, or the legacy default for its mode.

The layer is resolved again only where the step re-checks saved policy:

- A thermal request resolves once, before it stores its override. A
  document the domain decoder refuses then stores nothing, as on the fan
  path.
- A thermal pair resolves again only when the power class changes between
  its setters (`destination_holds`, through `PowerFence::admits`). A profile
  edit during a pair on one class does not stop it, as before.
- A fan operation re-checks saved policy before each setter in
  `FanPermit::authorize`. It resolves again on the first check and whenever
  the policy digest changed, so a profile edit during a fan request stops it.
  Its `destination_holds` also runs only on a class change between setters,
  as for a thermal pair.

The profiles are decoded from the committed `profiles.json` bytes with the
domain's ordered reader (`typed_profiles`), never through a `Value`. This
host's serde_json keeps no member order, so a `Value` writes members
alphabetically. The decoder needs a lighting `kind` discriminator first, as
managed does, so a round trip would refuse every saved Static, Breathing or
other effect with members after `kind`. Schema 26 gets the same
`PreserveLegacyIndependentProfileIntent` on the typed set that `aggregate`
applies to its `Value`. The domain decoder also refuses what managed
`ProfileStore` would not bind, such as an unknown member, so such an
aggregate resolves nothing and every profile read fails closed. The decoder
also binds a number written as a JSON string, as managed does: a saved fan
control with `"fixedSpeedRpm": "03700"` resolves to 3700, where the earlier
reader passed the stored member to `validate_settings`, which refused it.

Native applies no profile on unclassified external or unknown power, where
`Resolve` takes `CreateObservedFallbackProfile`: there only explicit overrides
select a destination, as before. It has no recovered HyperBoost layer either.
It never selects HyperBoost, and a resolved battery preset under confirmed
HyperBoost is refused as `Plan::NoDirectPath` (Blocked, "Leaving HyperBoost
for a battery preset needs the HyperBoost restore sequence, which this build
does not submit; no thermal setter was submitted."), whichever layer selected
it.

As in managed, a save that gives the active rule's path to a new id leaves no
application profile active until the next report selects the new rule.

The active pair keeps the reported spelling of the path, not the rule's. That
is visible only in the state: path comparison ignores case, so no resolved
output depends on it. A case-only change to the rule's path kept the selection
before this port too. What changed in the replay is the next report: it now
renews the lease, as managed's `SetApplicationContext` returns no change, where
the replay used to record a change.

`profile-resolution-vectors.json` pins it. `NativeProfileResolutionVectors`
drives `ProfileAutomationState` through reports, updates, power changes and
the unclassified projection, with and without manual fans on limited power and
confirmed firmware state. For each step it records the profile `Resolve` starts
from (on `ExternalUnknown`, the fallback profile, read through reflection),
`Resolve` with no manual override, and the behaviour. Each update step also
records the save-path predicate over that `Resolve`. Separate rows drive the
real `UpdateProfilesAsync` on classified power, recording its inputs and
whether the full transition ran. On unclassified power that transition never
runs, so the update steps are the evidence there. The Rust reader computes the
desired term with `desired_intent` and checks it against managed's.

Differences from managed:

- A reported path not already in `NormalizeExecutablePath` form is refused.
  Managed's IPC codec refuses the same paths (`IpcMessageCodec.cs:288-291`,
  `:1180-1194`), so this differs only for an in-process caller. The native
  check does not expand an 8.3 short name (`profile_set/text.rs:94-97`), so it
  accepts a short alias that managed would expand when it exists on disk.
- `desired_intent` is the profile layer only. The host adds manual overrides,
  recovered HyperBoost intent and the temporary battery overrides and full
  charge as described above.
- `ChangePowerSource`'s other effects (`ProfileAutomationState.cs:179-200`),
  expiring transient manual overrides and cancelling a temporary full charge
  on battery, are not ported; they belong with the host's overrides. The
  vectors record its return value, which the reader checks.
- The replay matches paths with the Windows ordinal comparison, as noted
  above; the typed binding uses managed's table.

### Request-driven reconciliation

The Cargo feature `service-profile-triggers-v1` composes it. No packaged
composition implies it, and both package readers (`native_package` and
`WindowsNativePackage`) refuse it by feature name and by its `build.rs`
capability marker: it has no low-power gate and no transaction-pool
reclamation yet. Without it, `updateProfiles` stores profiles only and
`reportApplicationContext` records the report only, as before the feature
existed.

With it, three requests apply saved intent, as managed
`ApplyFullTransitionAsync` does, for performance and fans only
(`Runtime::reconcile_locked` in `request_triggers.rs`):

- A foreground report (`Runtime::application`, managed
  `ReportApplicationContextAsync`) that changes the active pair. The reply is
  "The active application profile was recorded." followed by the hardware
  result. A stale sequence of the same reporter replies Stored "A stale
  application context was ignored."; a renewal of a reconciled pair replies
  Stored "The active application profile lease was renewed." or "No
  application profile is active." and applies nothing. A reporter outside the
  active local session is Blocked "Application context requires the active
  local Windows session."
- A report while the pair still owes a reconciliation. A `(sid, session)`
  owes one from Service start and after each change of its active pair or
  saved thermal intent (`Runtime::reconciled`). Only an outcome that may have
  submitted, which is never replayed, or that confirmed there is nothing to
  apply settles it (`Reconciled::settles`). A refusal that provably submitted
  nothing (unknown Windows power, a refused admission, an unreadable store, a
  pace refusal or an unclassified adapter) leaves it owed, so the next report
  reconciles. A fresh Service holds no temporary override, so none is replayed.
- A profile save (`Runtime::save_profiles_locked`, managed
  `UpdateProfilesAsync`) while an application profile is active, when the save
  changes the active id or the performance or fan members at a class fresh
  Windows power admits (`SaveIntent::thermal_changed`, over managed
  `activeApplicationIntentChanged`). The reply is "Profiles were stored."
  followed by the hardware result. A member an explicit override decides is
  masked, so an edit under that override is no change. A save that changes
  only lighting, battery policy or laptop behaviour has nothing native applies
  and replies Stored "Profiles stored without automatic hardware application."

Each trigger takes the mutation gate first and refuses (Blocked, nothing
recorded or stored) when its deadline passed while it waited. It applies only
when fresh Windows power is known and fresh admission holds (managed's
classified `ActivePowerSource`, `DeviceVerified` and `!WritesBlocked`);
otherwise the reply is the stored one. A report to a fenced store replies
Blocked with the store's reconciliation reason.

The steps are the explicit request's permits in reconcile mode: they store
nothing and validate no override, and they keep every per-write admission,
`destination_holds` re-check, independent readback, journal record and stop on
ambiguity. Before accepting, a step checks the override document as a save
would (`Storage::verify_overrides`): a document changed outside the Service
fences the store and the step replies Blocked "Saved configuration changed
unexpectedly; storage reconciliation is required. ..." before any submission.
A step whose own adapter reading finds unclassified power writes nothing
(Blocked, `UNCLASSIFIED_BLOCKED`); a step whose reading finds a classified
class the trigger did not change writes nothing either (`Classes`,
`Stepped::unchanged`). A failed step is recorded by its permit and never
replayed or retried. After a hardware result, a store that cannot be read for
the status projection fences writes and is reported (`after_write_unreadable`)
rather than ending the Service before the reply.

Each step journals under its own id (`step_request`): the first 16 bytes of
SHA-256 over `OpenBlade.ReconcileStep.v1`, the lowercased trigger id and the
step name, written as a version 8 GUID. The journal keeps one row chain per id
and refuses a second chain with another fan record, so the two steps of one
trigger cannot share the trigger's id. Version 8 keeps the ids distinct from
the version 4 ids managed clients send.

Fans go first only when the confirmed fans are manual (the last thermal
observation's fan mode, retained between observations as managed retains its
confirmed state, or an owned fan change not yet handed back) and every
class the trigger applies at returns them to Automatic (`fans_first`, managed
`DeviceSubsystemOrder.ForTransition`). That fan step reads firmware's manual
fans and carries the saved preset in the same pair, as an explicit return to
Automatic does; the performance step that follows finds it confirmed.
Otherwise performance goes first. Both steps run unless the store becomes
fenced. The reply status is managed's `ToOperationResult(intentStored)`
(`operation_status`): Applied when every step ran and each one that needed a
change was confirmed, else Stored for a save, else Failed when a step's
journal row was not known unsubmitted or the fence may hide a submission
(`Storage::submission_possible`), else Blocked. A performance step deferred
under manual fans is not counted as applied. The step messages are joined and
a message both steps return is said once.

`request-trigger-vectors.json` pins the parity. `NativeRequestTriggerVectors`
records managed's `ToOperationResult` over every combination of its inputs,
`ForTransition` over every confirmed and desired fan mode, and the reply
sequence of one reporter through a change, a stale report, a renewal, leaving
the rule, a report with no application and a caller outside the session.
`request_triggers.rs` replays them against the native runtime.

Differences from managed:

- Battery and lighting are not applied, and the lighting restore after a save
  is not composed.
- The status base reports 3 for any power but battery, and only a step's own
  adapter reading tells the two classified external classes apart. On AC a
  save is compared at both and reconciles at the classes it changed; a step
  whose reading finds the other class writes nothing, and the save replies
  Stored as managed does when its active class is unchanged. These
  comparisons put back any temporary override a read at the other class would
  retire.
- A report that cannot read the saved profiles keeps its sequence and the
  pair already selected and replies Blocked with the reason. Managed holds its
  profiles in memory and has no such case.
- Native has no low-power gate of its own here; each write's fresh admission
  is the boundary. Lease expiry and power changes trigger no reconciliation
  yet.

## Submission, ambiguity and finite transport lifetime

The portable typed pair resolves both 0D/02 reports before the first mutation.
Every admitted preset uses its destination directly when current admission
permits it. Exact0581 external-power current-manual plus effective-desired-manual
fan policy stores the request without setters; it does not restore per-preset
fans. The fan request that later returns the fans to Automatic applies it.
Automatic-active plus saved fixed fan intent remains direct. No blanket
Balanced/Automatic reset is an entry prerequisite.
The same native port worker and finite process transaction allocator are used;
all write, per-command power and final readback identifiers are reserved before
submission. Reopening a handle or replacing a worker does not reset that budget.

Performance and fan requests run for a 6 s recipe budget, the battery request's,
both at the request (`readiness::mutation_budget`) and in the mutation worker,
which otherwise caps a job at the 3 s observation bound. The 2 s frame deadline
was sized for a preset request: 4 snapshot and 11 recipe exchanges, about
133 ms each. The longest recipes are 4 + 17 exchanges: Custom from a Custom
baseline (the observation and its levels 4, the 07/8C read 1, four setters with
their power reads 8, the final pair and its levels 4), and a fan return to
Automatic with Custom or a manual entry with its intermediate pair. The fan
recipe also waits up to 210 ms after an entry state pair and 65 ms between target
commands. 6 s leaves those 21 exchanges twice the per-exchange time of the 2 s
deadline after the 275 ms of spacing. Each operation also checks its own
measured pace once (`Pace`), after its Prepared row and its first setter's own
07/8C reading and before that setter's intent row. With `E` exchanges and `A`
journal rows left, it runs only if
`now + slowest + (E - 1) × second + A × append + fixed` ends before the
deadline; `Limit::check` fails at the deadline itself, so a projection that
ends exactly there is refused. `slowest` and `second` are the two longest
exchanges so far, each timed from the previous reply (the first from the
operation's start) to its own reply. Each one therefore charges the work before
its send as well as the send, the settle wait and the reply: the discovery and
authorization before the first exchange, and each checkpoint's two
authorizations, custody check and origin verification. Only the Prepared
append is taken out of its exchange. `append` is that append, timed directly.
`A` is two rows per setter (intent and acknowledged) plus the closing row, and
`fixed` is the fan recipe's spacing. The projection excludes retirement after
the closing row, and it charges every remaining exchange at a measured one, so
work that grows after the check (a slower checkpoint, or an exchange with more
checkpoints than the measured ones) is not charged. One stall is charged once, so a single 07/8C read held up to
1 s, or a slow append that still fits, does not refuse a recipe with seconds to
spare. A device that is slow throughout has two slow samples by the check (the
recipe's first 07/8C reading and the first setter's own), so the second longest
carries it. A slow journal is charged for every row left. The earlier rules
failed both ways: the mean let one slow read or append through to an overrun,
the slowest interval between completions refused healthy recipes after a
single 500 ms stall and still let a uniformly slow device and journal start and
overrun, and timing exchanges from send to reply let Silent with a 210 ms
checkpoint, or Custom with a 126 ms one, start and overrun with setters
submitted. Otherwise the intent stays saved, no setter is submitted, retirement settles the Prepared row as known not
submitted, and the reply is Blocked with "The remaining request time cannot
cover this recipe at the measured device pace; no thermal setter was
submitted." (no fan setter, for a fan request). Owned Stop cleanup is not
paced. The refused recipe's transaction IDs are not reused: the pool is never
reclaimed within a process, and a refusal consumes no more of it than running
the recipe would have.

With `service-profile-triggers-v1`, `reportApplicationContext` and
`updateProfiles` run for an 11 s reconciliation budget
(`service_frame::RECONCILE_BUDGET`): the status snapshot and the longest
performance step are the 21 exchanges above, and the fan step that follows
under the same deadline adds up to 17 more. 38 exchanges at twice the 133.3 ms
frame exchange plus the 275 ms of fan spacing take about 10.41 s, which a
compile-time assertion next to `RECIPE_BUDGET` checks. Each step still refuses
at its own measured pace. These two requests are not canceled at their
deadline (`readiness::reconciles`): the runtime refuses before it records or
stores once the deadline has passed, and after that each step bounds itself,
so a result that finished late is still answered instead of closing the
connection after a commit. The native Agent's report waits for the budget and
one frame more (13 s, `REPORT_REPLY_BUDGET`). The managed tray's
`ServiceClient` bounds only its 2 s connect and waits for the reply without a
limit.

A fan operation's checkpoints reread saved policy, so that a change between
setters stops it. They resolve it again only when a digest of its inputs
changed: the override and configuration files, the override bytes this storage
accepted and both temporaries (`Storage::policy_inputs`). A full resolution
reads the aggregate once and decodes its profiles into the typed set
(`Storage::profile_layer`); the digest reads the five files once (about 1.2 us
in memory).
An unexpected Custom final selector rejects confirmation without allocating
unreserved Custom component queries. Ordinary full observation still reads both
Custom components. CustomManual High/High confirms the Custom performance and
Maximum fan selectors while CPU/GPU levels and the full
CustomPerformanceSettings remain unknown. The shared managed codec admits absent
settings only for confirmed Custom observation; desired and requested Custom
intent remains strict, and malformed present settings are rejected.

A machine-global thermal-mutation.journal lives under the SAME owner Store.
Per-user configuration adapters route its I/O through retained ReadCustody, so a
SID switch cannot bypass an earlier user's ambiguity. Each durable stage includes
original request ID, SID, peer PID/session, owner-generation digest, full typed
requested intent, intent hash, resolved destination, and ordered progress:
Prepared, FirstIntent, FirstAcknowledged, SecondIntent, SecondAcknowledged,
Confirmed (ordinals 0-5), plus KnownNotSubmitted (6), and for Custom
ThirdIntent, ThirdAcknowledged, FourthIntent and FourthAcknowledged (7-10),
between SecondAcknowledged and Confirmed. Every journal owner nonce is checked against the complete validated
Active owner history retained by the same Store, including legitimate historical
owners. Unknown owner references are refused on read and before replacement.
Intent is flushed and read back before each possible submission.
Prepared proves no mutation intent was reached. It stays fenced until actual
worker/transport retirement permits a checked Prepared-to-KnownNotSubmitted
append for the exact owner/request/user and unchanged journal prefix. FirstIntent
and later incomplete stages preserve possible submission. Persistence failures latch quarantine in memory and leave
partial staging on disk. Cancellation retains the actual worker until native
callback retirement; dropping a requester cannot release custody early.
Configuration replacement failure, accepted but ineligible intent, transaction
capacity, pre-submission refusal and possible submission have distinct outcomes.
A known-unpublished activation refusal may reconstruct a reader only after real
drain; uncertain publication retains the fence. Initial read admission precedes
ID reservation, and IDs already reserved are never reclaimed.

After ambiguity, explicit observation remains read-only. It can report current
thermal state but does not clear the durable mutation fence or replay an uncertain
request. Unresolved owner history cannot be repaired by guessing retirement.
Managed recovery accepts thermal journal history through the native-to-managed
handback described in [thermal history](windows-rust-thermal-history.md#native-to-managed-handback):
Setup records each unsettled or owned obligation of a crashed generation, and the
managed Service resolves it by fresh reads without replay. The handback is
one-way: no later native generation can own firmware state after it. Transport
retirement alone is not firmware reconciliation. Inherited manual-fan ownership
is reported, not adopted, and an interrupted journal rollover still refuses
retirement.

A separate read-parity gap remains: native observed HyperBoost combined with a
null broad capability snapshot does not satisfy the managed decoder's capability
requirements. This stage does not widen that validator or infer capabilities.
An explicit Automatic fan request therefore stays refused while HyperBoost is
observed, since its reply would publish that observation; owned Stop, which
publishes nothing, closes the owner with 51.
Full native-read/managed-codec parity, including that combination, remains future
work; the confirmed-only Custom correction grants no new write authority.

## Offline evidence boundaries

The focused native dispatcher tests use the actual installed dispatcher, runtime,
Storage, ReadSource, typed Reader and native worker lifecycle with explicitly
injected memory-only metadata, caller, custody, files and ports. Separate prelisted
own-user private-file tests use the actual ProtectedFiles and durable Store,
including first reservation, atomic journal replacement, exclusive sharing,
restart and cross-SID refusal. The profile transaction also runs on real files:
the eight tests in `profile_storage_real_tests.rs` drive the actual
ProtectedFiles, with only the security verifier injected, in a private directory
under the temporary directory, through a refused rename, leftover and held staging files, a recorded transaction
and exact-byte deletion. Constructors take those inputs before any default
root resolution. Real controller/token/session/SCM/HID suppliers are compiled,
not executed. Managed tests parse emitted native replies with the actual v85 codec
and inspect explicit artifact capabilities and conservative recovery refusal.
The native test marker emits the original received UTF-8 payload, preserving
property order and the leading polymorphic discriminator. Extraction retains
those payload strings unchanged; parsed/sorted JSON is not wire evidence.

Final evidence must bind the exact clean commit/tree, full compiler inputs and
outputs, actual feature/define sets, complete inspected test bodies, held harness
hashes, exact prelists and original logs/TRXs. Prior evidence is reusable only for explicitly unchanged inputs with recorded
equality; changed production paths require fresh focused evidence. Installation, hardware validation, lifecycle
experiments, package publication and default activation remain outside this stage.
