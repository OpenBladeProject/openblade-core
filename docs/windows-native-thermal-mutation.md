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

The installed v90 dispatcher accepts typed `setPerformanceOverride` and
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
per-write admission stays authoritative. `WriteScope::LowPowerCleanup` is
managed's `PriorAuthorityCleanup` exception for the low-power preparation: the
keyboard's Normal release and, after it, the exit of
[owned HyperBoost](#owned-hyperboost-service-hyperboost-v1). Native has no Fan
Stop guardian, so no fan cleanup runs
there. The gate admits it only while the exact suspend generation it captured
is current
(`IsActiveForGeneration`), so the next resume, DisplayOn or suspend ends it.
Only the Service's cleanup admission captures it (`CleanupGuard`, with
`CleanupScope::LowPower`; see Keyboard Driver mode ownership in
[the Service guide](windows-rust-development-service.md)). Owned Stop and
shutdown handback stay Ordinary, because managed blocks them in low power too.
D1 decision, revised: a low-power cleanup pins its suspend generation only
(`Lifecycle::fence_for`), as managed admits it on the generation and
ownership. The earlier epoch fence let a lock between the release setter's
acknowledgement and its readback turn a good release into uncertainty for the
rest of the process. Owned Stop and the shutdown handback keep the ordinary
epoch, which every notice moves. `low_power()` returns
the current transition for timers that ask outside a write, and `None`
(unreadable or exhausted) means low power. Timers that need only the answer
use `low_power_or_unknown()`, which is true for `None`. Request-driven
reconciliation reads it before each step, where that check is the refusal,
and compares its generation across a step to classify a refusal the step's
write admission made (see Low-power deferral under Request-driven
reconciliation).

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
setting read would only fence a second time and abandon work captured
between the two notices. That Windows sends `PBT_APMPOWERSTATUSCHANGE`
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

An automatic temporary-full-charge cancel on Battery survives a notice that
fences its tick. Every guard pins the epoch it captured and refuses once it
moves, so `battery_source_event` rereads the epoch after a refused capture or
authorization. A refusal while a later notice holds the epoch, or while low
power holds none, claims nothing, and the newer epoch makes the next tick read
the source again; a suspend is retried after resume. After the claim, the
worker's own per-write admission can fail the same way. The claim
(`TemporaryClaim`) takes the owner, which it marks abandoned, and its intent,
and records the cancellation reservation it leaves in place. A fenced attempt
gives the claim back for the next tick in one of two ways. An attempt proven
to have sent nothing (its worker joined, and either never began the steps
that send the cancel's IDs or left every one of them held in the transaction
pool, `Transactions::unsent`) is rolled back: the rollback puts back exactly
what the claim took, or nothing. It refuses over a newer owner or intent,
after an independent read found the charge off since its confirmed positive,
over quarantined or unsettled storage, and once the reservation the claim saw
was spent or replaced. An attempt that sent some of the reserved IDs is
restored for a fresh-ID retry instead, as described next. Each fenced retry
needs a newer epoch, and only an SCM notice advances one, so those retries
never outnumber notices. An attempt that sent nothing at the tick's own epoch
while its fresh admission still holds, such as one whose write port failed to
open, is rolled back the same way at most twice per enable
(`UNSENT_CANCEL_RETRIES`), and the tick's coalescing mark is cleared so the
next tick reads the source again. A refused admission at the tick's own
epoch, and a third such attempt, pause the cancel with its warning. A
rolled-back retry reuses the enable's reserved cancel IDs. The
enable keeps those IDs pinned until it settles: it has left every owner slot,
any claim on it is decided and its cancel has joined
(`BatteryPermit::settle_pins`).

The pool never issues an ID again once it was sent without a correlated
reply, because a retry could then accept the first attempt's late reply. A
restarted Service also quarantines, from the journal's active tail, a
still-live enable's `cancel_ids` and the newest temporary-charge request's
IDs. As far as its limit allows, it adds earlier `superseded_ids` sets (see
[thermal history](windows-rust-thermal-history.md)). So a
fenced attempt that sent some of the reserved IDs is retried with fresh ones
instead, when it joined, journaled no submission intent and was not
confirmed. `TemporaryClaim::roll_back_spent` puts the owner and intent back
under the same conditions as the unsent rollback, and storage
(`Storage::restore_spent_claim`) admits it only on settled, unquarantined
storage with no newer intent, when the attempt journaled nothing and left the
reservation unchanged, or its last row is its own known-not-submitted 6. It
replaces the spent reservation with the fresh-ID retry's
(`retry_capacity`, for the same enable) and marks the enable
`cancel_spent`. Every later automatic cancel of that enable draws its IDs
from the pool the way the enable drew the reserved ones
(`Reader::reserve_retained_pair`), and its record names the reserved IDs it
replaces in `superseded_ids` (see
[thermal history](windows-rust-thermal-history.md#temporary-charge-source-cancellation-ids)).
The Reader hands those IDs to the record once it names them (`retain`), and
the attempt settles them once it has joined; until then its retirement unpins
them. A fresh-ID attempt that is itself fenced after it sent anything pauses
like any other unconfirmed cancel, so one enable sends at most two ID sets,
and the journal refuses a second superseding request. An attempt that read
the charge off has independent evidence, so it is not restored either way.

An enable's cancellation reservation covers the retry: the larger of the
cancel's four rows and its Prepared and known-not-submitted rows followed by
the four rows of a fresh-ID retry at its largest (`cancellation_capacity`), so
a journal near capacity still holds the retry without rolling over or
refusing it. Another writer refuses while either reservation stands.

So of the 40 authorizations in each window map, the 7 before the first
request (the 07/8C power read) are retried with the reserved IDs and the 21
after it are retried with fresh IDs. The 2 after the submission intent
(progress 70) was journaled pause, even when the setter's pre-send checkpoint
refused, and the 10 after the setter was sent are never replayed. A refusal
at the tick's own epoch still claims the cancel once and pauses it.

The order of the intent row and the setter's checkpoint stays as it is:
checkpoint, row 70, then the setter's own pre-send checkpoint, then the send.
It is the write-ahead order every native setter uses. The intent must be
durable before the setter could be submitted, and the last admission check
must follow the append, so append latency cannot make it stale. An unsettled
70 fences the store, so no retry of either kind starts after it. Turning a 70
whose pre-send checkpoint refused into a known-not-submitted row would rest
on pool state that exists only in memory, and managed reads a 70 as pending.
The fresh-ID contract needs no change there.

Managed writes this cancel in the same
transition, since its Battery step has no write-block callback
(`ControlCoordinator.cs:1731-1738`).

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
  read battery. The Windows battery reading must hold until that setter's SET
  is sent, as Windows AC must for an external reading.

A class change between setters follows the rules under
[unclassified external power](#unclassified-external-power). Each setter is
admitted by its own reading, so a battery preset continues across an unplug
or a plug-in while its destination holds on the new class, as in managed, and
an AC preset stops on battery without replay. Saved-intent selection is
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
(`CanExitHyperBoostDirectlyTo`, `ProductionBatteryPort.cs:832-834`; the RZ09-0581
`HyperBoostBaseModeAdmissionSource` admits direct exits to Silent, Balanced,
Performance and Custom only). A direct exit to Silent keeps the first
reading's classified external class, as the Performance exit does. Without
`service-thermal-hyperboost-exit-v1`, native stores a battery request under
confirmed HyperBoost and replies Blocked without preparing a row
(`Plan::NoDirectPath`).

#### HyperBoost exit to a battery preset (`service-thermal-hyperboost-exit-v1`)

The feature implies `service-thermal-mutation-v1` and enables
`openblade-domain/hyperboost-exit`. It ports managed
`ExecuteThermalSettersAsync` (`ProductionBatteryPort.cs:2277-2528`) with its
`hyperBoostExitActions` (passed at 806-811 for a battery destination):

1. Managed classifies the adapter once for the exit (2324-2336) and needs
   full, limited or battery. Native takes this from its first 07/8C reading,
   which must agree with Windows, as for any battery preset.
2. The `hyperboost-restore-balanced` pair
   (`Rz09_0581_02e0_4_00_HyperBoostProduction.json:28-48`; the same payloads as
   `performance-balanced-channel-1/2` at
   `Rz09_0581_02e0_3_01_BatteryProduction.json:179-196`), written at 2337-2397.
   `PresetPair::hyperboost_restore` encodes it, and a protocol test compares
   it with that fixture and with the Balanced pair.
3. An independent full thermal read (2399-2408). Anything but Balanced with
   Automatic fans and HyperBoost inactive stops as indeterminate:
   `thermal_mutation::restored`.
4. The battery pair (2428-2495), then the final read (2501-2526), which only
   the battery preset with Automatic fans confirms.

That is 12 exchanges and four setters. Each setter keeps native's own 07/8C
reading, fresh Windows check and fence admission (`PowerFence`), and the pace
check before the first setter counts the whole recipe, so a recipe that cannot
finish inside the operation budget is refused before any intent row. The row
journals 0, then 80-83 (restore intent and acknowledgement per setter), 1-4,
then 5; `Prepared` reserves every transaction ID first. Managed decides the
plan from the port's own fresh read, and native from the reading the request
was resolved on: when firmware has already left HyperBoost the request is the
direct battery pair. When firmware leaves HyperBoost for anything other than
Balanced after the restore pair, the intermediate read fails in both and the
row stops at 83. A possibly submitted setter, a failed or unconfirmed
intermediate read and a final mismatch each leave the row unsettled; later
requests and a restart are fenced, and nothing is replayed.

Native differs from managed here in these ways:

- Managed admits no command of the recipe after classifying the adapter: no
  per-command ordinary admission after the exit (2450,
  `ordinaryControl && !exitedHyperBoost`). Native reads 07/8C before every
  setter and admits it as it would admit that battery preset's own command.
  An unplug, a plug-in or a full-to-limited change between or during the pairs
  therefore continues a persistent request, as managed's recipe does. A
  reading that disagrees with Windows, an unclassified reading, or a Windows
  kind change across a setter's query or between its reading and its send
  stops it where managed completes: at the first setter nothing is submitted
  and the row retires; at a later one the last acknowledged row, 81, 83 or 2,
  stays unsettled. A classified source change retires a temporary override
  (a shortcut's included), so the request no longer stands and the exit stops
  at the setter whose reading saw it, in the same way. An unclassified
  reading is refused by the battery preset's class rule first and retires
  no temporary.
- Managed's exit classification ignores Windows; native refuses a class that
  disagrees with Windows (for example a battery reading under Windows AC)
  before storing, as for every battery preset.
- On a failure managed replies Failed, `Performance override intent was
  stored. Hardware result: Performance write is indeterminate: <stage
  message>` and reconciles from its own fresh readback, leaving no write
  pending. Native replies Blocked with the reconciliation reason and fences the
  store until read-only reconciliation, the existing deviation for an
  uncertain submission (see [reply parity](#reply-parity-with-managed)).
- Storage reserves a battery row's longest legal chain, the restore chain,
  even for a direct battery pair, which leaves four rows unspent.

`ServiceControlRuntimeTests.NativeHyperBoostBatteryExitVectors` records
managed's reply and pending state for HyperBoost to each battery preset on
Battery, PluggedIn and UsbC, on success and on each stage's failure with
either readback
(`tools/openblade-windows-host/tests/data/hyperboost-battery-exit-vectors.json`),
and `hyperboost_exit::managed_hyperboost_battery_exit_vectors_replay_through_the_dispatcher`
replays every row. Success matches managed's status and setter sequence; the
failure rows pin the deviation above. Both package readers refuse the feature
and its marker by name ("The HyperBoost exit is not admitted for
packaging."). The recipe is source-reviewed only: it has not been run on a
laptop.

#### Owned HyperBoost (`service-hyperboost-v1`)

The feature implies `service-thermal-hyperboost-exit-v1` and enables
`openblade-domain/hyperboost`. It serves the `setHyperBoost` operation
(`enabled` true or false) on exact RZ09-0581 and ports managed
`SetHyperBoostCoreAsync` (`ServiceControlRuntime.cs:6840-7857`) with the
port's `SetHyperBoostCoreAsync` (`ProductionBatteryPort.cs:869-940`). A request
is one performance step under the thermal gate, with the same caller, custody,
fresh exact-device and controller admission, durable journal, per-setter power
admission, GPU clock fence, independent readback and stop-on-ambiguity as every
other native thermal write (`hyperboost_mutation.rs`).

Entry writes the `hyperboost-enable` pair, 0D/02 `[1, 1, 7, 0]` then
`[1, 2, 7, 0]` (`Rz09_0581_02e0_4_00_HyperBoostProduction.json`), which
`PresetPair::hyperboost_enable` encodes; a protocol test compares it with that
fixture. Only an independent read of HyperBoost with Automatic fans confirms
it. The row is destination 100 with no requested override and journals 0,
1-4, then 5. Exit writes the saved destination at the fresh class: on
RZ09-0581 every saved AC base is a direct exit (`CanExitHyperBoostDirectlyTo`),
so native writes that destination's own pair once, and a saved battery preset
takes the [restore recipe](#hyperboost-exit-to-a-battery-preset-service-thermal-hyperboost-exit-v1).
An entry stores no intent; an exit stores none either and restores the saved
one.

Only an entry needs an admitted saved intent. As managed submits every exit and
skips only its restore step (`restoreIntentSkipped`, 7118-7127), an exit whose
saved intent native cannot restore, a saved GPU clock effect or no saved
destination at the class, still leaves HyperBoost. It writes managed's unowned
exit, the Balanced restore (`ownedHyperBoostBaseMode ?? Balanced`, 7066; the
`hyperboost-restore-balanced` pair has the Balanced pair's payloads), as a
Balanced row with no requested override, and settles on an independent read of
Balanced with Automatic fans. The saved intent and the desired state it projects
are unchanged, and the reply, after managed's skipped-restore reply (7833-7835),
says so: `HyperBoost is off and the laptop stays in Balanced until the power
source changes.` followed by what was not applied. Managed's exit makes no
ordinary per-command power admission, so each setter of this exit is admitted on
a classified class that agrees with Windows, battery with Windows battery
included (`thermal_mutation::admits_restore_class`), and a class change keeps
it. The reply names the reason found at the class the exit resolved; a
classified class change between setters keeps that reason, as managed resolves
its restore intent once. The journal, GPU fence, custody, device, pad and
readback checks all still apply.

An outstanding owned fan change refuses every exit before any row, whatever the
saved intent: the thermal history admits no performance row while a fan owner
is outstanding, so the row would leave the journal unreadable and owned Stop
unable to hand the fans back. The exit checks it before resolving the saved
intent, and the thermal store also refuses to append a performance row while a
fan owner is outstanding. The reply is `HyperBoost was not turned off because
OpenBlade still owns an earlier fan change. The fans are already on firmware
Automatic. Restart OpenBlade to hand them back, then turn HyperBoost off.`
HyperBoost reads Automatic fans, so owned Stop closes the change without a
setter, and the exit is admitted after the restart.

The gates run in managed's order, each citing its managed line at the native
gate:

| Managed | Gate | Native reply |
|---|---|---|
| 6943-6952 | Probes unavailable (no composed pad and journal reader) | Blocked, `HyperBoost control is unavailable right now.` |
| shared | Exact device, controller, custody, quarantine, fresh 07/8C class with Windows agreement, pending journal, GPU fence | The shared thermal refusal |
| native only | Managed's ownership journal neither absent nor Relinquished, or unreadable | Blocked, the journal-pending reply below |
| 6965-7002 | Already in the requested state | Applied, `HyperBoost is already active.` or `HyperBoost is already off.`, before the GPU fence and with nothing prepared |
| 7004-7023 | Pad path selection | A connected pad refuses: native admits the pad-less path only |
| 7024-7032 | Fresh confirmed state | Holds for every reading the Reader decodes |
| 7134-7140, first for an unclassified external reading | Managed resolves ExternalUnknown's intent and then refuses the source; native's saved layer has no destination for that class | Blocked, `HyperBoost requires a confirmed external power source.` |
| 7121-7132 | Entry only: admitted saved intent at the class, no outstanding owned fan change, a saved aggregate, no GPU clock effect | The fan handback reply, or Blocked, managed's intent text. An exit refuses the fan change first, with its own reply, and otherwise skips the restore (above) |
| 7134-7140 | `PowerSourcePolicy.IsExternal` | Blocked, `HyperBoost requires a confirmed external power source.` |
| 7142-7159 | Fresh pad absence | Blocked, managed's pad-absence text |
| port 914-922 | Entry base: Silent, Balanced, Performance or Custom with Automatic fans, or manual (Maximum) fans | Blocked, managed's precondition text |
| 6806-6812 | Per-command revalidation before each enable setter | Pad absence again, and the journal before every setter of either direction |

Each gate has a concrete source reason: managed's own check at that line, or,
for the journal, managed's ownership of its own recovery
(`ReconcileUnexpectedExitAsync`, 11456-11648). No gate is a prerequisite
transition: entry goes directly from every admitted base, and exit goes
directly to the saved destination.

The cooling pad (1532:0F43) is observed read-only by the present HID
interfaces' hardware IDs, without opening any interface
(`discovery::hardware_id_present`). Absence needs a complete scan with every
row and ID list readable. A pad, a failed or incomplete scan, or a pad that
appears before either enable setter refuses. Native never writes an accessory
command or assumes a pad. The pad-present path is not admitted: it needs the
pad's interface contract, which is not reviewed for native use.

Journal decision. Native keeps its own row family and hands back to managed
through the existing handback (option b), rather than writing managed's
`hyperboost-ownership.json` (option a). Native rows are ordinary thermal
history rows with destination 100, so the existing frontier, fence,
reconciliation and package readers carry them without a second durable store,
and native never writes a file whose recovery managed owns:

- Native honours managed's journal. Before the request and again before each
  setter, native reads `hyperboost-ownership.json` read-only and proceeds only
  when it is absent or a recognized Relinquished document
  (`hyperboost_journal::journal_safe`, managed
  `IsSafeMaintainHyperBoostJournalBaseline`). Anything else, or a read that
  fails, replies Blocked, `HyperBoost recovery from the previous OpenBlade
  service is pending. Start that service once so it can finish, then try
  again.` The reader fails closed: managed matches member names in any case,
  takes the last of duplicate members, ignores unknown members and reads
  numbers from strings, so native accepts only the camelCase members managed
  writes, each once (no two names equal ignoring case at any depth), with JSON
  numbers for the document's own members, every member type-checked in every
  schema, the settings records bound as stored profiles bind them (which, as
  managed does, read an integer from a string), no recovery baseline, and the
  update time as a full timestamp with its offset. `NativeHyperBoostJournalVectorsTests` records
  managed's verdict on journals its store writes and on edits of them
  (`tools/openblade-windows-host/tests/data/hyperboost-journal-vectors.json`);
  `managed_journal_vectors_replay_through_the_native_reader` asserts that native
  never admits a journal managed refuses and names each case it refuses that
  managed admits.
- Managed recovers after a native run. `WindowsNativeThermalHistory` admits
  destination 100 rows only from an artifact whose capabilities name
  `service-hyperboost-v1`. The handback classifies an unsettled HyperBoost row
  as `pending` family `hyperBoost` (progress 1-4) or `abandonedPrepared` (0).
  Managed latches a pending HyperBoost confirmation for it, matched by
  `MatchPendingHyperBoost` against HyperBoost active with Automatic fans, and a
  disagreeing thermal reading is a mismatch. An exit's rows are ordinary
  Performance rows; a reading of HyperBoost, which reports no preset, resolves
  them as a mismatch (`WriteConfirmationPolicy.MatchNativeHandback`), so an exit
  stopped before its first setter is reported for a reapply instead of waiting.

Native differs from managed here in these ways:

- Managed retries a KnownNotApplied setter once; native retries nothing.
- An unconfirmed final read replies Blocked with the reconciliation reason
  and fences writes, where managed replies Failed after its own fresh
  readback.
- A connected pad refuses entry; managed enters through its pad path.
- Native exits restore the saved performance destination only, and the reply
  says `HyperBoost is off and the prior <name> performance is restored.`
  Managed restores the saved performance and fan intent. A saved manual fan
  intent is left to the fan writer. The reply carries no
  `HyperBoostRestoredPerformance`.
- Entry from a confirmed Custom whose saved intent is not Custom enters;
  managed cannot build a well-formed ownership journal for that pair and
  fails before any write.
- Controller and custody refusals use native's shared replies.
- An exit on an external source neither side can classify is refused
  before any write by both. Managed names its preserved ownership journal;
  native gives its shared unclassified-power refusal.
- A class change between the request's reading and a setter's reading stops
  an entry, because HyperBoost is never the saved destination.
- An exit whose saved intent carries a GPU clock effect writes the Balanced
  restore and says the tuning was not applied, where managed, which has a GPU
  clock writer, restores the saved preset with its tuning.
- An exit with no saved destination at the class writes the Balanced restore
  and says so, where managed says the saved setting isn't available.
- An outstanding owned fan change refuses the exit and asks for a restart,
  which managed has no counterpart for.
- Native answers `HyperBoost is already active.` or `HyperBoost is already
  off.` while a GPU clock confirmation is pending. Managed's WritesBlocked
  refusal (6943-6952), which counts an unconfirmed write, comes first and
  replies `HyperBoost control is unavailable right now.` Neither writes.
- Native ownership is derived from its thermal history, not held in memory
  or a second file: native owns HyperBoost while the active journal tail ends
  with a HyperBoost row settled Confirmed and no later performance row
  settled Confirmed for another destination, nor a later HyperBoost row
  settled KnownNotSubmitted (`thermal_history::hyperboost_owned`,
  `Storage::owns_hyperboost`). Managed holds `ownsHyperBoost` in memory beside
  its ownership journal. A HyperBoost reading native does not own stays
  external and excluded, as before. A rollover seals rows into a segment, so
  a session whose entry rolled out reads as not owned and is no longer exited
  by a lifecycle boundary; an explicit exit still works.
- The history's ownership counts only while no reading since the entry has
  shown firmware out of HyperBoost (`Tracker::left_hyperboost`,
  `Runtime::owns_hyperboost`). Managed clears `ownsHyperBoost` at any
  published observation with HyperBoost inactive (18394-18405), and its
  explicit disable that finds HyperBoost already off clears it and retires
  the journal (6965-6994). Natively, every reading the drift tracker observes
  and an explicit exit that replies `HyperBoost is already off.` end the
  ownership for every lifecycle exit: the battery boundary, Stop and suspend.
  A HyperBoost that appears afterwards was entered outside OpenBlade and is
  neither exited nor adopted. The journal is settled with the HyperBoost row
  Prepared and KnownNotSubmitted at the next admitted write: the explicit
  exit's own capture, the next battery boundary, or the Stop or suspend
  cleanup. A read holds no write admission, so until then only the process
  knows. The boundary's read-only adoption below stays for an entry no
  earlier reading contradicted.
- A restarted Service starts with nothing observed, so its first reading
  decides: HyperBoost keeps the journal's ownership, and anything else ends
  it. Native cannot tell a HyperBoost entered outside OpenBlade while the
  Service was down from the one it entered. Where the earlier process had
  seen HyperBoost off and could not settle the journal before it stopped, a
  HyperBoost active at the restart's first reading reads as owned and is
  exited at the next boundary. The journal stays unsettled only where no
  write was admitted: an unsettled row, an owned fan change the Stop's
  handback could not close, no cleanup admission (a competing controller,
  unknown Windows power, a lease that never activated), a journal segment
  with no room, or an entry another user made. The adapter's power class is
  not among them: the settlement sends no setter and needs none.
- The battery boundary (managed `ApplyFullTransitionAsync` setting
  `pendingOwnedHyperBoostPowerBoundaryExit` on Battery, then
  `ExitOwnedHyperBoostForPowerSourceTransitionAsync`) runs in
  `reconcile_locked` before the performance and fan steps, whenever an
  unscoped reconciliation's classes hold Battery: a lifecycle power change, a
  resume, a report or a save. It is H2's exit step, with all of its admission,
  under the step id `hyperboost`. The transition goes on only once the exit
  settled; a refused, unfinished or possibly submitted exit stops it. A
  refused exit keeps ownership and is tried again at the next boundary, not
  within the attempt; a possible submission fences the store without replay.
  A convergence attempt never runs it.
- Firmware already out of HyperBoost at the boundary is not written to. As
  managed's `RestoreFirmwareExitedHyperBoostForPowerSourceTransitionAsync`
  marks the reading intentional, native makes the exit's fresh reading
  OpenBlade's own for performance and fans (`Tracker::own_reading`), so the
  transition after it applies the battery intent rather than pausing on
  drift. Native then settles ownership in its journal with a HyperBoost row
  Prepared and KnownNotSubmitted under a fresh write capture; managed retires
  its ownership journal. An outstanding owned fan change, a refused capture or
  a fenced store leaves the journal for the next admitted exit to settle. The
  reading has ended the ownership either way, so the transition goes on and
  its own steps name whatever refuses them, such as the outstanding fan
  change.
- The boundary needs the console user's admitted write capture, as every
  native reconciliation does. An unplug at the lock screen exits at the first
  admitted attempt after the unlock; managed exits at once.
- At the coordinated Stop or shutdown and at a confirmed suspend, native
  exits owned HyperBoost under the Service's cleanup admission
  (`hyperboost_cleanup.rs`), which the keyboard release and the fan handback
  hold. Neither boundary admits an ordinary write: SCM STOP and SHUTDOWN
  revoke the loaded origin before the drain (`scm_host::installed_notice`),
  and the low-power gate fences the ordinary epoch. Managed exits there
  through `RestoreOwnedHyperBoostForLifecycleCoreAsync`
  (`PrepareForShutdownAsync`, 13313-13390, and `PrepareForLowPowerCoreAsync`).
  Native differs from it in these ways:
  - The exit writes only managed's unowned exit, the Balanced restore pair,
    and settles on its own read of Balanced with Automatic fans. Managed
    restores the saved intent (`restoreUserIntent: true`). No native cleanup
    resolves or applies saved intent (the fan handback applies no saved
    destination either), and a shutdown or low-power cleanup reaches the
    firmware handoff directly (AGENTS.md, Direct transitions). The saved
    intent is unchanged. After a suspend the session that entered owes its
    reconciliation, which the tick after the resume runs from the Balanced
    the exit left as OpenBlade's own; after a Stop the next start's
    activation applies it. With the session locked that waits for the
    unlock, as every reconciliation does.
  - The order is the keyboard release, at Stop the fan handback, then
    HyperBoost. Managed exits HyperBoost first, on its caller's budget, and
    gives the keyboard and the fans a separate reserve. Native has one
    deadline, the drain's or the 1.5 s suspend window, so the exit takes
    what the others left. An exit ahead of them could spend their time, and
    one stopped after a setter leaves an unsettled row, at which the release
    and the handback both refuse. The handback also closes an owned fan
    change first, behind which the history admits no performance row.
  - Every row and exchange is authorized afresh under the cleanup admission
    of its scope: origin and package trust, the lifecycle fence, a known
    Windows power kind, competing-controller absence and the exact original
    custody. Managed's ownership journal is read before the request and
    before each setter. An outstanding owned fan change, an unsettled journal
    row and the GPU fence refuse it. Each setter is admitted on a classified
    class that agrees with Windows (`admits_restore_class`), behind its own
    durable row, and settles on its exact acknowledgement and the
    independent readback. The admission names no console user, as the other
    cleanups do not.
  - The recipe is paced to the deadline before its first row
    (`Pace::covers_before_rows`): one that cannot fit writes nothing. Its
    rows must fit the active journal segment as it is. A cleanup never rolls
    the journal over, which only the ordinary admission may do.
  - The rows are journaled for the session that entered, which the owning
    entry row names, through the store of the latest capture. A process that
    captured no store reads no ownership: it exits nothing and its Stop
    succeeds. An entry another user than the latest capture's made is
    refused, since that store cannot write its rows, and so is the exit of a
    process whose mailbox lease never activated, as for the other cleanups.
  - Firmware already out of HyperBoost is settled read-only, as at the
    battery boundary, on the exit's own reading and before any power check:
    the settlement sends no setter, so an adapter 07/8C cannot classify, or
    a battery reading under Windows AC, does not refuse it. The explicit
    exit still refuses the latter before it reads the mode
    (`battery_reading_contradicts_windows`). The reading stays an external
    change: managed only retires its journal there, and no transition
    follows.
  - A setter that may have been submitted leaves its row unsettled, which
    fences every write. Nothing is replayed, by this exit, a later suspend or
    the next process.
  - A Stop fails, as an unconfirmed fan handback does, when the HyperBoost
    it owns was still on at its reading, or could not be read, and it did
    not exit it: a refusal, an exit the drain had no time for, or an
    unsettled submission. The ownership stays in the journal. A Stop that
    finds firmware out, an ended ownership or nothing owned succeeds,
    whether or not the journal could record that end.
  - The suspend exit is composed with the keyboard owner
    (`service-keyboard-driver-mode-v1`), which carries the suspend
    preparation. Without it only Stop and the battery boundary exit.
- Native has no display-off exit. Managed exits owned HyperBoost at a
  display-off (`PrepareOwnedHyperBoostForDisplayOffAsync`,
  `ServiceLifecycleRefreshLoop.cs` 139). Native's power-setting reader reports
  only DisplayOn; the off transition is C2 of the lifecycle port, and it must
  not become a gate signal. A Modern Standby display-off sends no
  `PBT_APMSUSPEND`, so owned HyperBoost stays active through it until the
  battery boundary, a suspend, a Stop or an explicit exit.
- After a native Stop, managed adopts no native ownership, and the handback
  record carries no ownership item (option b). Managed's own shutdown exits
  its HyperBoost before handing back, its crash recovery exits at its next
  start rather than adopting a session, and the handback already declines
  inherited fan ownership. A native session left active, by a refused Stop
  exit or a crash, is HyperBoost active without a managed ownership journal, which
  managed treats as an external change; a native restart reads its ownership
  again from the journal.
- Without the lifecycle triggers, the native composition has no HyperBoost
  probes, so every request replies `HyperBoost control is unavailable right
  now.` before any read or write, and nothing natively enters HyperBoost
  that no boundary would leave.

##### Status members and the recovery request

The managed contract carries the three HyperBoost status members and
`recoverHyperBoostRestore` at protocol 90, so native fills and serves them
with no protocol change. The metadata observer reads the members with each
status, on its own thread and for every caller
(`status_source::observe_hyperboost`):

| Member | Managed | Native |
|---|---|---|
| `padlessHyperBoostEligibility` | A pad observation with each verified device observation whose capabilities name a HyperBoost access (`ResolvePadlessHyperBoostEligibility`, 22990-23013) | The pad probe the entry gates read (`Probes::eligibility`): CoolingPadAbsent, CoolingPadPresent, or Unknown for a failed or incomplete scan |
| `hyperBoostRestorePending` | Its ownership journal is RestorePending: an exit whose saved performance and fan intent was not confirmed restored (10116, 10143) | Managed's journal names that state (`hyperboost_journal::restore_pending`) |
| `hyperBoostBlockingControllers` | The controller observation's HyperBoost blockers that the status also lists as potential controllers (16139-16145) | The same observation's `hyperboost_blockers`, filtered and spelled the same way (`HyperBoostStatus::blocking`) |

Native differs from managed here in these ways:

- The pad and the journal are read only on the host matched as RZ09-0581,
  and only where the composition has the HyperBoost probes, which the
  lifecycle triggers compose. Managed keys the pad on a capability record;
  the native status carries none, so the matched host stands in for it and
  the member is published before any device is opened. Every other host, a
  composition without probes and a status published before any observation
  report Unknown and no pending restore.
- Native has no pending restore of its own. A native exit is one journaled
  performance step: refused before a setter, it leaves HyperBoost on and
  owned; possibly submitted, it leaves an unsettled row that fences the
  store, which the handback gives managed as a pending confirmation rather
  than a restore. The member therefore reports managed's journal only.
- The journal's state is read without managed's `IsWellFormed` verdict. A
  RestorePending document managed would call invalid, and so report as not
  pending, reads as pending natively. Both refuse every HyperBoost write.
  An absent journal, every other state and a document that names no state
  report none, as managed does for an invalid journal (10505-10518).
- A journal that cannot be read establishes neither answer, so the status
  keeps the one it last published (`HyperBoostStatus::resolved`), as managed
  preserves its presentation when its durable state cannot be read
  (10133-10152). One failed read neither hides a pending restore nor invents
  one. With no earlier answer the restore reads as pending, as managed's
  recovery treats a journal it cannot inspect (4057-4069) and as every
  native HyperBoost request treats it. A status that observed nothing keeps
  the last answer too and establishes none of its own, so an unreadable
  journal after a first observation that yielded nothing is still pending.
- The pad scan and the journal read run on the observer's thread after its
  other checks of the cancellation, so neither starts once it is set, and
  the pad scan takes the flag itself. A refresh past its budget and a drain
  both end the scan.
- A request's gates hand the scan their job's cancellation the same way
  (`Permit::resolve`, `Permit::admits_setter`, `Probes::pad`), which a
  dropped request and the Stop's `Worker::stop` set. A cancelled scan is a
  failed one: the entry refuses, with no row at the request or with its
  Prepared row retired before a setter, and the job thread is free for the
  keyboard release, the fan handback and the exit. The scan checks the flag
  between its enumeration calls (`discovery::scan_hid`), so one call that
  never returns still holds its thread. On the job thread that is the
  mutation worker's limit: the fan handback waits for the job, and the Stop
  ends at its bound with that step and the ones after it not run.
- The status observer scans processes only, so it lists no blocking service
  and always carries the detection warning, which the tray treats as a
  block. Each write still takes its own controller admission. A status
  published before any observation carries managed's null, which leaves
  consumers the name policy (`RazerThermalControllerPolicy`). The managed
  codec refuses blockers that are not a sorted, distinct subset of the
  status's potential controllers, which `blocking` keeps.

`recoverHyperBoostRestore` ports the admission of managed
`RecoverHyperBoostRestoreAsync` (4040-4555) and none of its writes. Managed's
retry re-applies the performance and fan intent its journal saved, against
the recovery baseline that journal recorded, and then retires the journal.
Native never writes that journal (the journal decision above) and has no
restore of its own to retry, so the request reads managed's journal once,
under the operation gate, and answers. It sends no setter, appends no native
row and publishes no status:

| Managed | Journal | Native reply |
|---|---|---|
| 6943-6952 | No probes composed | Blocked, `HyperBoost control is unavailable right now.` |
| native only | The caller is not the active interactive user | Blocked, `The active interactive Windows user is required.` The request reads no journal; the status the dispatcher takes before every mutation has read it, and scanned the pad, for any caller |
| 4082-4091 | Absent | Stored, `There is no saved HyperBoost restore to retry.` |
| 4093-4104 | EnablePending, OwnedConfirmed or Relinquished | Blocked, `The HyperBoost ownership journal is not awaiting a user-authorized restore retry.` |
| 4053-4080, 4108-4555 | RestorePending, unreadable, or a document that names no state | Blocked, the journal-pending reply above |

Native differs from managed here in these ways:

- Managed retries a RestorePending journal once; native refuses it and names
  the previous service, which owns that recovery.
- Managed replies Failed when the journal cannot be inspected and Blocked
  with its own text for an invalid journal; native gives the journal-pending
  reply for both.
- The state is the one the document names (`hyperboost_journal::state`).
  Managed also answers that the journal is not awaiting a retry for a
  RestorePending journal without a restore intent, and its invalid-journal
  text for a document of any state that `IsWellFormed` rejects. Native
  tells neither apart: the first gets the journal-pending reply, the second
  the reply of the state it names. Every one of these is a refusal that
  writes nothing.
- An unsettled native row does not change the answer, and the answer does
  not change the row: the fence stays until the handback, in this process and
  after a restart.

A native retry of an uncertain exit would need a journal row that settles an
earlier owner's unsettled row from a later read. The row format admits none
(a row of another request cannot follow an unsettled one), and adding one
changes the format the package readers and the managed handback read. That
is a separate decision; this stage adds no such row.

Packaging stays refused (`service-hyperboost-v1` by name, in
`native_package.rs` and `WindowsNativePackage.cs`). The status members and
the recovery request above report an owned session and a pending restore.
A package is still blocked by the following:

- The Stop and suspend exits have not been timed on a laptop, so a package
  could leave HyperBoost running through a suspend or a Stop its window could
  not cover.
- The status carries no writable HyperBoost capability: `capabilities` is
  null, or the observation record beside an active HyperBoost. The managed
  tray therefore offers neither the toggle nor the retry against a native
  Service.
- `validatePadlessHyperBoostLifecycle`, managed's developer validation
  request, is not served.
- A result carries no `HyperBoostRestoredPerformance`.
- There is no display-off exit.

`ServiceControlRuntimeTests.NativeHyperBoostOwnedVectors` records managed's
reply and writes for entry and exit from each confirmed state the getter
decodes, on PluggedIn, UsbC and Battery, with the pad absent, plus a present
and an unobservable pad, and on an external source the adapter table cannot
classify
(`tools/openblade-windows-host/tests/data/hyperboost-owned-vectors.json`).
`hyperboost_owned::managed_owned_hyperboost_vectors_replay_through_the_dispatcher`
replays every row and asserts each deviation above literally. Both package
readers refuse the feature and its marker by name ("Owned HyperBoost is not
admitted for packaging."). The feature is source-reviewed only: it has not
been run on a laptop.

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
`ValidateUnclassifiedPerformanceOverride` does. Non-null `gpuClockOffsets`
blocks with the GPU cause unless `service-gpu-clock-tuning-v1` composes the
writer, which applies them as managed's legacy enabled tuning
([GPU clock tuning](#gpu-clock-tuning-service-gpu-clock-tuning-v1)).

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
admits them unchecked. Each setter's query is bracketed by Windows samples,
as managed brackets it (`AdmitOrdinaryThermalCommandAsync`): the sample before
the query must admit the command, so Windows battery refuses an ordinary
command and an unknown kind refuses every command before the query is sent,
and the sample after the reply must equal it. A kind that changes and returns
while the query is in flight is seen by neither. The presence a reading
was admitted with must hold at every later checkpoint until that performance
setter's SET is sent, or for a fan operation until its end (managed
`OrdinarySourceUnchanged`), so losing it stops before the command is sent. A
class change between setters, either way
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
status base reports 1 for battery and 3 for any other power, Unknown included.
It is the Windows power the metadata last observed: every 2 s, and once per
lifecycle epoch at the origin tick (see
[Power change after conflicting adapter classes](#power-change-after-conflicting-adapter-classes)),
so it may be older than a read served before that tick. Status reads saved intent at that class only
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
- A fan operation requires the presence a reading was admitted with at every
  later checkpoint of the operation; managed requires it only until that
  command's own SET. A Performance entry admitted on Windows AC whose later
  commands see Windows battery completes its four writes in managed and stops
  in native. A performance setter holds it only until its SET is sent, as
  managed does.
- Managed admits a fan command with no reading check while Windows reports
  battery; native still reads 07/8C before each setter and stops on a battery
  reading, which no fan destination holds, and on an unclassified reading,
  which needs Windows AC. Managed's Balanced handback there writes both
  setters without an adapter query. Native also stops such a fan command when
  the Windows kind after its reply differs from the one before its query,
  which managed does not check there.
- A fan return to Automatic that carries a saved battery preset needs fresh
  Windows AC presence in native. Managed applies the carried preset through
  `SetPerformanceAsync`, which admits a battery preset's selectors on Windows
  battery unchecked (`allowBattery: IsBatterySelectorCommand`).
- A battery preset's setter on Windows battery: managed admits it without an
  adapter query; native queries 07/8C and needs a battery reading, with
  Windows battery before the query and after the reply.
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
- Native status carries no `externalPowerPresent` and no capability set:
  `capabilities` is null except beside a confirmed active HyperBoost, which
  carries the observation record (no control admitted). With no
  override its desired provenance stays the profile default; managed reports
  the confirmed firmware state.
- Managed also admits lighting and battery controls on unclassified power;
  those are not native thermal setters and are unchanged.

#### Power change after conflicting adapter classes

Managed latches a power-only write block when a reading's adapter classes
conflict with Windows power, and a classified power change releases it,
applies the destination and publishes the classified source (`1a03e358`,
`3f7b3838`, `ChangePowerSourceCore`). Native latches nothing. Every step
reads 07/8C fresh, and a reading that conflicts with Windows power (a battery
class under Windows AC, or an unclassified one without Windows AC presence)
refuses that step with nothing submitted and nothing published. The cause
stays owed, so the next lifecycle epoch's classified reading applies the
destination as any power-kind change does (see
[Lifecycle reconciliation](#lifecycle-reconciliation)).

The native port is the published source. Each origin tick's
`process_power_source` observes Windows power once per lifecycle epoch, before
the tick reconciles (`GuardedStatus::publish_power_source`), so the
destination's publication carries the new source instead of the source of the
last 2 s metadata refresh. An epoch moves on every SCM power, session and
suspend notice. Metadata that repeats the last publication publishes no new
revision. One attempt settles the epoch: a refresh that yields no evidence is
not retried, because each attempt can hold the source lock for the 1 s refresh
budget, and the 2 s subscription refresh publishes the source instead. A tick
whose temporary-charge source event already attempted the observation does
not repeat it. A failed observation closes the feed, as a failed subscription
refresh does. The source admits no thermal write: every step still takes its
own fresh 07/8C reading and Windows power. The Agent's display automation and
display-power admission do act on the published `activePowerSource`
(`session_native/display_query/automation.rs`,
`session_native/installed_monitor.rs`). A rejected or timed-out 07/8C
read at the change writes nothing, still publishes the Windows source, and
the next notice's classified reading applies the destination once the
debt's retry pace has passed.

`power-only-release-vectors.json`
(`ServiceControlRuntimeTests.NativePowerOnlyReleaseVectors`) records the
managed transitions, and `readiness_tests/power_only_release.rs` replays them.
Differences from managed:

- `writesBlocked` stays true: native publications never report thermal write
  authority open, where managed reports false after the release.
- The refused step publishes nothing, so during the conflict status shows the
  Windows source (3), where managed retains PluggedIn (0).
- Native has no transient-read label, so both managed release cases replay
  the same native case.
- The block another owner holds is a store fenced by an earlier submission
  that may have reached the device, not an experimental write pause. Its
  source is still published, and nothing is written.
- `unconfirmedWrites` is not published. An unresolved destination submission
  keeps the store fenced, and a read-only observation and a later notice
  replay nothing; managed reopens write authority there.
- An explicit request served before the next tick publishes the source of
  the last observation.
- Managed has a second release path: its periodic 15 s observation
  (`ServicePeriodicObservationLoop`, `RefreshPowerSourceClassificationAsync`)
  also reaches `ChangePowerSourceCore`. Native retries the owed step only at
  a later lifecycle epoch, and the debt settles after `RETRIES` (2) retries
  (`request_triggers.rs`). If the first 07/8C reading after a plug-in
  conflicts and no further notice arrives, native keeps the previous profile
  until the next notice or request, where managed applies the destination
  within about 15 s. A periodic native 07/8C observation is tracked
  separately.

Resolution uses eligible manual intent, then the current application report's
performance override, then the matching power profile. Desired performance
provenance is the application (source 2) when its override sets the preset,
Custom levels or GPU tuning, as managed `Resolve` does. The managed vector case
"an override of only custom levels or only gpu tuning is an application source"
(`profile-resolution-vectors.json`, `NativeProfileResolutionVectors`) records
it. This intentional parity fix to the status projection came with request
triggers but is not gated by their feature. Application instances have
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

Without `service-gpu-clock-tuning-v1`, configured GPU clock effects are saved
without a thermal pair and reply Blocked with managed's committed-intent prefix,
`Performance override intent was stored. Hardware result: GPU clock handling is
incomplete...`; with it, the explicit requests apply them
([GPU clock tuning](#gpu-clock-tuning-service-gpu-clock-tuning-v1)). Manual fan
application, display, lighting, and other native controls are not implemented
here. Their saved fields are not confirmation. The broad capabilities snapshot
remains null. Applied means this bounded thermal preset was independently
confirmed, with GPU/broad capability incompleteness stated in the result. An
ineligible saved request also replies Blocked with that prefix, even when the
policy-selected preset is confirmed; the hardware result then says that the
policy selects another destination. Requested override, effective
selected value/provenance, and observed state remain distinct. Mutation success
frames contain only the operation result, as required by the unchanged v90
single-body response contract. Status travels separately through GetStatus,
ObserveState and subscriptions.

### Reply parity with managed

Native replies follow managed's status and message shape for saved but
unapplied intent:

| Native case | Reply |
| --- | --- |
| External power (PluggedIn, limited USB-C or unclassified) with current-manual plus desired-manual fans, when a native destination resolves | Stored, `Performance intent was stored and will be applied when fans return to Automatic.` |
| The same manual-fan state with no native destination (for example a clear under a High/High Custom profile) | Blocked, `Performance override was cleared. Hardware result: No supported performance destination...` |
| Confirmed HyperBoost and a Battery Saver or Battery Balanced destination, without `service-thermal-hyperboost-exit-v1` | Blocked, `Performance override intent was stored. Hardware result: Leaving HyperBoost for a battery preset needs the HyperBoost restore sequence, which this build does not submit; no thermal setter was submitted.` |
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

### Performance shortcut (`service-performance-shortcut-v1`)

A Service built with `service-performance-shortcut-v1` serves
`cyclePerformanceShortcut` and `setPerformanceShortcutPreview`, managed
`CyclePerformanceShortcutAsync` and `SetPerformanceShortcutPreviewAsync`. The
feature implies `service-fan-mutation-v2`, so a cycle under manual fans uses
managed's Stored reply. It adds no firmware command: every destination a
cycle selects is one of the preset recipes and the Custom recipe above.

A cycle is an explicit performance request whose override is staged inside
`RequestPermit::resolve`, after the fresh 07/8C class and 0D/82 reading and
after that class retires any temporary it ends. The staged override then takes
every check, save, journal row, readback and stop-on-ambiguity path of
`setPerformanceOverride` under the same operation gate. The next preset comes
from the shared domain `performance_shortcut::next`, which replays managed
`PerformanceShortcutSelection` from
`tools/openblade-windows-host/tests/data/performance-shortcut-vectors.json`
(`ServiceControlRuntimeTests.NativePerformanceShortcutVectors`):

- Classified external power (PluggedIn or limited USB-C) cycles Balanced,
  Silent, Performance, Custom, Battery Balanced and Battery Saver; Windows
  battery cycles Battery Balanced and Battery Saver. The cycle starts from
  the preset managed `Automation.Resolve` selects there: the saved override,
  then the resolved profile, then, with no profiles saved, managed
  `ProfileDefaults.CreateSafeDefaults`'s power profile, whose Balanced makes a
  first press on either adapter select Silent. It starts from the confirmed
  preset instead when confirmed drift has marked Performance as changed
  outside OpenBlade.
- Unclassified external power cycles the ordinary presets from the
  confirmed preset, offers Custom only with an admitted ordinary Custom intent
  (the saved override's settings without legacy offsets, then the confirmed
  levels, then managed's default) and stages no GPU tuning. Confirmed
  HyperBoost, or a battery reading under Windows AC or unknown power, is
  Blocked with `Hardware application was not confirmed. The external power
  adapter could not be classified; the performance shortcut is blocked.`
  A fixed maximum confirms no preset and is Blocked with `The current thermal
  state is unavailable for ordinary performance cycling.`
- The staged override lasts until the power source changes
  (`UntilPowerSourceChange`) and replaces the saved performance override, as
  managed's single override slot does. A Custom destination carries the power
  profile's Custom settings, or managed's default.
- An Applied reply reads `The performance button selected <preset>.` or
  `Fn+P selected <preset>.`; a Stored reply under manual fans adds
  ` It will be applied when fans return to Automatic.` Every other reply,
  including a rejected combination (Failed) and the low-power deferral
  (Blocked), keeps the performance request's own text.

A preview stores no intent and reads or writes no firmware, so it takes no
operation gate. The Reader holds one preview and publishes it as
`performanceShortcutPreview` with every active status; inactive projections
omit it, which reads as managed's null. A clear removes only the same
session's same generation, and a preview replaces any preview but a newer
generation of the same session, with managed's three Stored replies. Session
GUIDs compare without case. The codec refuses an undefined source, the nil
session, a generation below 1 and an undefined preset.

Differences from managed:

- Native cycles from the fresh 0D/82 reading on unclassified power and when
  drift marks Performance, where managed uses its cached confirmed state.
- On classified power native stages the resolved tuning, as managed stages
  `current.DesiredState.GpuClockTuning` (`ServiceControlRuntime.cs:2742`,
  `:2818`): the eligible persistent override's, then the eligible temporary
  override's, then the active profile's, then the selected Custom settings'
  legacy offsets as enabled tuning (`Storage::gpu_tuning`, after managed
  `ProfileAutomationState.Resolve`). Without `service-gpu-clock-tuning-v1`
  any of them reaches the GPU refusal.
- Under the low-power gate a cycle reads no firmware and stages from the
  saved intent at Windows battery. Managed also honours its cached
  `ExternalChanges` and `ConfirmedState` there; the native runtime keeps no
  confirmed reading to select from.
- Windows battery with an external 07/8C reading is managed's Battery
  source, where managed cycles the battery presets. The native thermal
  worker refuses every performance request there before anything is staged,
  an explicit one included.
- Native runs no full transition when the shortcut finds a changed power
  source: temporaries expire through the class observation instead.
- The native status message is rendered from status, not replaced by the
  shortcut's reply.
- Without the Reader (no thermal custody) a preview is Blocked with
  `Performance shortcut previews are unavailable.`, managed's
  `IServiceRuntime` default text. A preview from outside the controlling
  interactive session is Blocked before it changes anything.
- Managed queues a preview behind its single user-request gate, so it waits
  for a cycle or other user request in progress. Native applies it at once.
  Both publish the same preview afterwards. The preview publication retains
  no read: the Reader's read witness and retention window stay those of the
  last thermal read.
- The HyperBoost exit to a battery preset follows an explicit request: the
  stored `NoDirectPath` refusal, or with `service-thermal-hyperboost-exit-v1`
  the restore recipe (see
  [the HyperBoost exit](#hyperboost-exit-to-a-battery-preset-service-thermal-hyperboost-exit-v1)).
  A cycle that selects a battery preset under confirmed HyperBoost writes the
  four setters.

Both package readers refuse the feature and its marker by name ("The
performance shortcut is not admitted for packaging.").

### GPU clock tuning (`service-gpu-clock-tuning-v1`)

The feature composes the RZ09-0581 NVML writer (`thermal_gpu.rs` over
`gpu_clock_offsets.rs`) into the explicit performance requests:
`setPerformanceOverride` and the performance shortcut. A profile save
(`updateProfiles`) is a reconciliation, so it keeps the GPU refusal with the
other reconciliation paths below. Its image carries the `service-gpu-clock-tuning-v1`
marker, and both package readers refuse the feature and the marker by name: a
WinVerifyTrust and catalog check of the loaded `nvml.dll` path is still
required before packaging. NVML loads from System32 only. There is no durable
journal for GPU offsets (parity with managed). Stop, shutdown and low power
restore nothing, as in managed.

The decisions follow managed `ControlCoordinator.ApplyPerformanceOutcomeAsync`
(`ControlCoordinator.cs:591-698`) and the RZ09-0581 provider's paired and
GPU-only setters (`WindowsVerifiedBladeDeviceProvider.cs:1780-2050`):

- No tuning relinquishes management without restoring the driver (`:2052-2121`).
  An unreadable uncertain submission refuses the step.
- A confirmed preset with matching offsets writes nothing; with other offsets
  it takes the GPU-only setter and writes no firmware row
  (`CanApplyGpuClockOffsetsOnly`).
- Otherwise the paired write preflights the offsets against the policy
  envelope before any firmware row: a refused preflight stores the intent and
  replies Blocked with managed's `Performance is unavailable: <message>`
  (`CapabilityRefused`), or Failed with `Performance write was not applied:
  <message>` (`WriteRejected`). A disabled request reuses a confirmed reset
  (`PairedReuse`).
- An NVML failure after the firmware half is indeterminate with managed's
  `The power mode was applied, but GPU clock tuning failed.` detail, and is
  resolved by one fresh read, never replayed (`ResolveIndeterminateWriteByReadbackAsync`,
  `:3172-3268`). Indeterminate and not-applied results reply Failed (status
  3) and an unavailable GPU-only setter replies Blocked, each after the same
  committed `Performance override intent was stored. Hardware result: `
  prefix (`ServiceControlRuntime.cs:14989-14991`).
- While a GPU submission is uncertain, or an NVML call that outlived its request
  is still running, every native thermal writer refuses before any row or setter
  (managed `PendingWriteConfirmationTracker.Blocks`). With
  `service-thermal-hyperboost-exit-v1`, the restore recipe that leaves
  HyperBoost for a battery preset is refused with them, before its first row,
  and its battery destination takes the GPU half a direct step to it would. An
  explicit performance step replies `A thermal write requires read-only
  reconciliation before the performance command can be submitted.` A fan
  request, a return to Automatic and owned Stop's handback reply `A thermal
  write requires read-only reconciliation before another command can be
  submitted.`; an explicit fan request stores its intent first and prefixes that
  result as its other hardware results. A reconciliation is refused at its
  admission, before any firmware read, and what it owes is kept, as managed
  keeps its pending transition for after the confirmation resolves
  (`ServiceControlRuntime.cs:16985`, `:18200-18241`): a report replies Blocked
  with its own prefix and that message, a profile save is Stored with `Profiles
  were stored.` and that message, a lifecycle attempt keeps its cause and runs
  again at the first tick after the fence clears, convergence reserves nothing
  and retires a due retry Blocked unrun, and `reapplyChangedSettings` replies
  its pending-confirmation block (`:3842`). In low power a trigger's fence
  starts no GPU call and fences on the recorded state alone, as managed's
  tracker is in memory. Once a read resolves the management, the next report or
  lifecycle retry applies the owed intent. A resume withdraws a confirmed reset,
  and a power change or a change between classified sources (PluggedIn and
  USB-C, `ServiceControlRuntime.cs:18684`) allows one more ungated query
  (`:2123-2180`).

Native deviations:

- The NVML preflight runs on the HID job after the intent is saved and every
  firmware refusal has passed; the NVML submission runs after the independent
  firmware readback and re-checks write admission, where managed submits
  before its final read.
- Resume invalidation and the power-change rearm are applied at the next GPU
  call, from the gate's clear generation, the Windows power generation and the
  power class the explicit step resolved, where managed applies them when the
  event arrives.
- The pending block is the management machine's own uncertain state and its
  outstanding calls, checked on the shared Reader write path
  (`Permit::thermal_fenced`) after one read-only resolution attempt within the
  request; managed keeps a separate `pendingWriteConfirmations` tracker. The
  attempt first crosses the gate and Windows power generations its writer or
  trigger was admitted under, so after a power change the first fence takes the
  rearmed query, where managed rearms at the event. Owned Stop's settle-only and
  handback permits build their fence at Stop, after the change that moved the
  owner's epoch. A lifecycle cause the fence refused is checked against the
  fence at each tick, where managed reruns its deferred transition after each
  observation. A fence resolves no power class and keeps the class last seen, so
  a change between PluggedIn and USB-C within one Windows power generation is
  rearmed only by the next explicit performance step. The writers are released
  only by a finished read that leaves nothing uncertain, never while a call
  runs.
- An explicit fan request stores its intent before the GPU fence refuses it,
  where managed's `ScopedThermalWriteBlockReason` refuses it before storing.
- A reconciliation under a pending GPU clock confirmation is refused before it
  reads anything, where managed's transition reads its pre-write state and
  keeps each subsystem as deferred and not submitted. Its reply names the
  pending confirmation alone, without any step's reply.
- Each NVML call is bounded by what remains of the request less a 500 ms reply
  margin, and by 5 seconds; a paired step reserves 1 second of the pace for
  its submission. A call that never started refuses the step. A call that did
  not finish replies Failed and indeterminate before the deadline, keeps
  running on the worker, and fences every thermal writer until it ends.
- Triggers, profile saves, convergence, fan handback and drift reconciliation
  keep the GPU refusal. Managed's fan handback preserves GPU management
  (`ApplyPerformancePreservingGpuClockManagementOutcomeAsync`).
- `Storage::gpu_incomplete` treats a saved GPU clock effect as one at every
  power class, where managed selects no tuning off external power. That
  battery case is left with the reconciliation paths.
- `setGpuClockTuning` and the status projection of confirmed offsets are not
  served yet.

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
  no Fan Stop guardian; `setBatterySaverFanStop` refuses it the same way
  (`service-fan-stop-v1`). Managed allows it once Fan Stop is admitted at
  run time.
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
([Request-driven reconciliation](#request-driven-reconciliation)), and the
origin tick applies it after a resume, a Windows power-kind change or lease
expiry ([Lifecycle reconciliation](#lifecycle-reconciliation)).
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
it. With `service-thermal-hyperboost-exit-v1` it runs the restore recipe
instead, whichever layer selected it.

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
capability marker: no packaging stage has admitted it, and its lifecycle
reconciliation is not validated across a physical sleep and resume.
Without it, `updateProfiles` stores profiles only and
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
- A report while the pair still owes a reconciliation. A `(sid, session)` owes
  one at every class from Service start and after each change of its active
  pair or saved thermal intent (`Runtime::debts`, `Debt`). A refusal before any
  step (unknown Windows power, a refused admission, a fenced store, a pending
  GPU clock confirmation) reads no firmware and leaves the debt as it was.
  Every reconciliation whose steps ran settles it, as managed attempts each
  change once, with one exception: when each step that needed a change was
  confirmed or was refused for a cause that may clear by itself
  (`Stepped::transient`: the measured pace, an adapter not classified yet, a
  store or profile read that failed, or time or admission that ran out before
  the intent row), no row of it was possibly submitted and the store is not
  fenced, the debt stays owed for at most two later reports (`RETRIES`). One
  change therefore reads firmware for at most three reconciliations, though a
  renewal arrives every 5 s; an unbounded debt would read firmware and draw
  transaction pairs on each one. A fresh Service holds no temporary override,
  so none is replayed.
- A profile save (`Runtime::save_profiles_locked`, managed
  `UpdateProfilesAsync`) while an application profile is active, when the save
  changes the active id or the performance or fan members at a class fresh
  Windows power admits (`SaveIntent::thermal_changed`, over managed
  `activeApplicationIntentChanged`). The reply is "Profiles were stored."
  followed by the hardware result. A member an explicit override decides is
  masked, so an edit under that override is no change. A save that changes
  only lighting, battery policy or laptop behaviour has nothing native applies
  and replies Stored "Profiles stored without automatic hardware application."
  The save owes its changed classes together with any debt from before it. A
  step whose reading finds a class the save did not change writes nothing and
  leaves the earlier debt as it was; otherwise the earlier debt is settled only
  when a step's reading found one of its classes, since the steps there apply
  that class's whole saved intent.

Each trigger takes the mutation gate first and refuses (Blocked, nothing
recorded or stored) when its deadline passed while it waited. It applies only
when fresh Windows power is known and fresh admission holds (managed's
classified `ActivePowerSource`, `DeviceVerified` and `!WritesBlocked`);
otherwise the reply is the stored one. A report to a fenced store replies
Blocked with the store's reconciliation reason.

#### Low-power deferral

Managed `ApplyFullTransitionAsync` checks the `LowPowerGate` before any device
access and returns Blocked "Lighting intent was stored and will be applied
after standby." (`DeferLightingIntentForLowPower`), and each thermal write
checks it again (`ScopedThermalWriteBlockReason`). Native refuses the same
writes through the admission it already has: the trigger's capture
(`WindowsGuard::capture`, which requires an active lifecycle) and
`fresh.authorize()` before any firmware read or transaction pair, each
step's capture and authorization, and each write's
`WindowsGuard::authorize_for`, which compare the pinned epoch with
`epoch_for(WriteScope::Ordinary)`, `None` while the gate is set.
`Runtime::gate` reads `Lifecycle::low_power` (wired by `Runtime::native`);
an exhausted or unreadable lifecycle counts as low power, which managed's
gate has no state for. It never admits a write.

A trigger, save or explicit override that arrives while the gate is set is
refused by the write capture, which requires an active lifecycle, and then
takes the storage-only capture (`WindowsGuard::capture_storage`, through
`Runtime::capture_intent`). That capture admits storing intent and nothing
else. It is an `Identity` without a lifecycle epoch (`StorageGuard`), so it
cannot hold a read or write proof; a `WindowsGuard` always pins one. It keeps
every Windows supplier check of the write capture except the epoch
(`Identity::admits`): origin and package trust, the mutation artifact, the
custody's integrity and health, the same console session unlocked, the same
primary SID and logon LUID, a known AC line status, and fresh
competing-controller absence. It also needs the custody's own
original-identity admission (`ReadCustody::authorize_storage`). It names no
device: the exact HID identity belongs to the mailbox custody, admitted when
the Service opened it, and only a write's own `authorize` reaches that
custody's reader. Every step and write still takes the full capture,
`authorize` and the per-write epoch check, unchanged. The storage capture is
taken only when the gate reads set and the write capture's refusal was not
fatal. A store fenced by an earlier submission (`Storage::pending`) or a
saved document the domain decoder refuses still stores nothing.

The storage capture skips one awake refusal: a Reader that cannot activate
its mutation owner (activation known unpublished, or uncertain and fenced)
refuses the awake request before anything is stored, while in low power no
activation is attempted, so the request stores. The reconciliation it owes
after the gate clears is refused at the same activation, writes nothing and
settles the debt. Storing intent is not a write, so this is kept.

The deferral then runs as it does when the gate sets after the capture:
nothing is read, the debt stays owed with no attempt spent, and the first
report after the gate clears applies the saved intent once. The gate does
real work only when it sets in the middle of a trigger:

- before each step (`request_triggers.rs`, the check ahead of the step's
  request id). That check is the refusal: a suspend after the first step
  stops the second before its capture, reading or pair.
- after a step, when the gate's generation moved during it, so a suspend
  that has already cleared again still counts. A step that was refused
  (Blocked) or did not finish by the bound, with nothing of it possibly
  submitted, no class found unchanged and the store not fenced, is
  relabelled as the deferral. A relabelled step's own refusal is not
  reported for that reconciliation; the deferred step runs again once the
  gate clears. A step that may have submitted keeps its own result and is
  never relabelled. A moved generation proves only that a gate boundary
  arrived during the step: usually a suspend, possibly cleared again, but
  a new resume (more than 2 s after the last boundary, `NEW_RESUME_TICKS`)
  moves it by two without setting the gate. Treating a step refused
  across that resume as a deferral is benign: nothing of it was
  submitted, and the debt stays owed for the next report. A step relabelled
  at the bound is not counted as unfinished, so its reply matches a
  deferral at the step's check; a save, for example, stays Stored rather
  than Failed.
- after the entry `fresh.authorize()` of a report or save refuses, when the
  gate sets between capture and that check. A changed pair on classified
  power replies Blocked "The active application profile was recorded.
  Lighting intent was stored and will be applied after standby."; a save
  replies Stored "Profiles were stored. Lighting intent was stored and will
  be applied after standby." A renewal and a report with no application
  reply as they do without the gate, as managed's do.

A deferred step adds managed's per-write reply, Blocked "The laptop entered a
low-power transition before the thermal command was submitted.", to the
steps that ran, and does not count as a step that ran. A deferral keeps the
debt owed with its attempts unchanged (`Reconciled::deferred`,
`Reconciled::spent`), even after an earlier step that may not be retried, so
renewals every 5 s in low power read no firmware, reserve no pair and never
exhaust `RETRIES`. After an earlier step that may have submitted, the retry
runs that step's class again once the gate clears. It reads firmware
first: while firmware still holds the target, the step confirms without a
setter and journals nothing; if firmware lost it, for example to a reset
in standby, the step applies it again under a new journal id. That is a
fresh application after a fresh read, not a replay of the earlier
submission.

Two debt drops were left by the low-power slice:

- A step that may have submitted and has not handed back by the bound
  ends the reconciliation, as every unfinished step does, so the debt
  settles and a later class is not applied after resume. This is the
  unfinished form of the deferral after a possibly submitted step above.
  It is kept: owing that class again would re-run a step whose
  submission is unknown, and the store stays fenced until read-only
  reconciliation settles the row. Once it has, a lifecycle power-kind
  change or lease expiry owes every class again, as does the next changed
  report.
- A fencing notice that does not move the gate generation, such as a
  repeated resume or a power-status change inside a step, refuses that
  step's write admission as lost admission rather than a deferral. Such a
  step, refused with nothing of it possibly submitted while the
  lifecycle's ordinary write epoch moved (`Runtime::admission_epoch`,
  `Lifecycle::active`), now counts as transient: the debt stays owed with
  one of its `RETRIES` spent. A lifecycle attempt keeps its cause and the
  tick retries it under fresh admission once the epoch has moved (see
  Lifecycle reconciliation); otherwise the next report applies it. The
  epoch only classifies a refusal the write's own admission made.

Explicit `setPerformanceOverride` and `setFanOverride` sent while the gate
is set store their intent through the storage-only capture, as managed
stores the override before its write admission refuses
(`Runtime::store_performance_deferred`, `Runtime::store_fan_deferred`). The
request is validated as on classified power (`combination_rejected`,
`override_rejected`) and stored at the battery class, nothing is read or
submitted, the pair owes a reconciliation at every class with no attempt
spent (`Runtime::owe_stored_intent`), and the reply is managed's: Blocked
"Performance override intent was stored. Hardware result: The laptop
entered a low-power transition before the performance command was
submitted." or "Fan override intent was stored. Hardware result: The laptop
entered a low-power transition before the thermal command was submitted.",
with "... was cleared." for a clear. A fan return to Automatic from saved
manual intent names the saved preset first, "Fans could not return to
Automatic with the saved BatterySaver performance intent.", as managed's
`ApplyDirectAutomaticFanTransitionOutcomeAsync` does when its performance
step is refused. Every check and read of the store runs before the save,
including the fan settings it leaves (`Storage::plan_fan_save`), and the
save and the debt are recorded together: a request that fails before the
save stores nothing and owes nothing, and nothing after it can fail. The
awake fan request reads the same plan before its save, so profile fan
settings the domain decoder refuses (a fixed speed above 5400 RPM, for
example) store nothing there either.

An explicit request whose write capture and authorization passed, but which
the gate overtakes after its store, is the same deferral. Its final
authorization (performance) or its unconfirmed result (fans) is refused, and
`Runtime::overtaken_by_gate` applies the rule a reconciliation step uses:
the gate generation moved since the request's own authorization, nothing of
it can have been submitted (`submission_possible`: no journal row of the
request past a known-unsubmitted intent, and no fence that may hide a
submission), and the store is not fenced. The request then replies as one
stored while the gate was set and owes the same reconciliation. A request
that may have submitted keeps its own result and is never relabelled. The
request's Prepared row settles as KnownNotSubmitted through the custody's
retirement append, as fan and battery rows do; unlike a plain journal
append, that append needs no live lifecycle epoch, so neither the gate nor a
stop that ends the request leaves its store fenced. A fan reply that is not
Blocked, such as a clear Stored on unclassified power, keeps its reply.

Four differences from managed remain:

- Native declines the adapter reading in low power. Production Windows
  reports AC to managed only as `ExternalUnknown`, so managed samples the
  adapter from firmware while its gate is set; when that reading
  classifies, managed stores the override, and when it does not, managed
  stores nothing and replies with the same Blocked "Hardware application
  was not confirmed. The external power adapter could not be classified;
  overrides and control writes are blocked." that native gives. The
  recorded `pluggedIn` rows come from a test double that classifies the
  adapter. Native admits no firmware read while the gate is set, so on AC
  it always takes the unclassified reply and stores nothing; only Windows
  battery classifies power without a reading. This fails closed and is
  kept.
- A fan request while OpenBlade still owns a fan change that has not been
  handed back stores nothing and replies Blocked "Fan intent was not stored:
  OpenBlade still owns a fan change that has not been handed back to
  firmware Automatic, and the laptop is in a low-power transition." The
  handback it owes needs a write, which the gate refuses.
- When the last transition ran on AC and the laptop is unplugged after the
  gate set, managed's next `ApplyFullTransitionAsync` is interrupted by the
  gate and does not store an explicit override that arrives then
  (`ServiceControlRuntime.cs:14483-14489`, `:16360-16364`). Native classifies
  the request at the Windows battery reading, stores it and owes the
  reconciliation. Storing is not a write: the stored intent is applied only
  after the gate clears, through a fresh capture, a fresh adapter reading
  and every per-write admission. There is no safety reason to drop it, so
  native keeps it.
- The stored intent is applied by the lifecycle tick after the gate clears,
  or else by the next report, at most one 5 s lease renewal later. Managed
  reconverges after resume through its convergence loop (below).

Under the default `ThermalControlPolicy.MaintainDesiredState`
(`BatteryAutomationPreferences.cs:18-19`), managed reconverges thermal intent
after resume: `ServiceSessionPowerTransitionCoordinator.cs:334` calls
`HandlePowerSourceChangedAsync` (`ServiceControlRuntime.cs:12321`), whose
observation reaches `EvaluateThermalConvergenceObservationAsync`
(`ServiceControlRuntime.cs:17599`, `:19943`), and the 15 s periodic
observation (`ServicePeriodicObservationLoop.cs:7`) repeats it. Only Strict
waits for a power-source change, which runs `ApplyFullTransitionAsync`.
Without `service-thermal-convergence-v1` native has no convergence loop; the
lifecycle tick after the gate clears reconciles a deferred debt in its place
([Lifecycle reconciliation](#lifecycle-reconciliation)). A deferral leaves the
attempts unchanged, so that retry spends none of the budget.

`low-power-trigger-vectors.json` pins the replies.
`NativeLowPowerTriggerVectors` records managed's reply sequence of one
reporter in low power (a change, a renewal, leaving the rule, no
application) and after the gate clears, a save, the startup transition, the
per-write refusal, and explicit requests in low power on AC and, after a
source change with the gate clear, on battery, with the setters each
submitted. `request_triggers_low_power.rs` replays the report rows over the
rig's default `Fresh`, which does not pin the lifecycle at capture, and
again under the production-modelled capture (`CapturedFresh`, falling back
to `CapturedStorage`), where they match managed's replies too. The battery
explicit rows are replayed under the production-modelled capture with
managed's messages; the AC rows keep managed's status and setters and reply
as unclassified power.

The steps are the explicit request's permits in reconcile mode: they store
nothing and validate no override, and they keep every per-write admission,
`destination_holds` re-check, independent readback, journal record and stop on
ambiguity. Before accepting, a step checks the override document as a save
would (`Storage::verify_overrides`): a document changed outside the Service
fences the store and the step replies Blocked "Saved configuration changed
unexpectedly; storage reconciliation is required. ..." before any submission.
A step that saved intent alone refuses at every class fresh Windows power
admits reads no firmware and reserves no transaction pair
(`refused_by_saved_intent`): a performance policy with a GPU clock effect or
no eligible destination, and fan intent with no native plan (ManualCurve, or
manual fans on battery). It replies the refusal the step would reach after
its reading. Each class is read in its own `Storage::unexpired` scope, as a
save's comparison is: a read at one class expires a temporary override kept
for another, so a temporary saved on the limited adapter is still selected
at that class after the full adapter's class was read.
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
- A deferral in low power is retried by the lifecycle tick after the gate
  clears, or else by the next report. Managed reconverges after resume under
  its default MaintainDesiredState policy and reapplies only on a
  power-source change under Strict
  ([Lifecycle reconciliation](#lifecycle-reconciliation)).
- The margin bounds a step, not the wait for the mutation gate. A trigger
  queued behind owned fan cleanup (`stop_fans`) or a battery power-source
  event (`battery_source_event`) waits for that work, as every mutation
  does, and refuses before it records or stores once its deadline has passed.
  Its reply can then arrive after the Agent has stopped waiting.

#### Lifecycle reconciliation

With `service-profile-triggers-v1`, the serving loop's 500 ms origin tick
(`serve_connections`, after `process_power_source`) also runs
`StatusSource::reconcile_lifecycle`, which calls
`Runtime::reconcile_lifecycle` (`lifecycle_triggers.rs`). It applies what the
console pair owes after four lifecycle transitions, as managed runs
`ApplyFullTransitionAsync` after them without an explicit request:

- A gate boundary: the gate generation moved through a resume, a new resume
  or a DisplayOn that cleared a set gate (managed `ProcessNativeAsync`, which
  `WindowsServiceLifecycleEventSource.cs:354-364` also dispatches for that
  DisplayOn). A boundary owes nothing by itself. It runs a debt the pair
  already owes, such as intent deferred or stored in low power.
- A Windows power-kind change, AC to battery or back
  (`Lifecycle::observe_power_kind`, sampled on every SCM power notice and
  once when `Runtime::native` starts): the pair owes every class, as managed
  `HandlePowerSourceChangedAsync` applies on a changed source. A change
  while the gate is set is owed and applied after the boundary that clears
  it, as managed skips `PowerChanged` in low power and compares the source
  again on resume.
- The 15 s application lease (`APPLICATION_LEASE`) expiring with an
  application profile active (managed `ExpireApplicationContextAsync`): the
  reporter's foreground is cleared and its pair owes every class, so the
  power profile applies.
- A console user other than the last one activated, including the first one
  after the Service starts. Managed `ActivateUserAsync` applies a new active
  user's profile at that user's first admitted request
  (`ControlRequestHandler.cs:210-220`, `ServiceControlRuntime.cs:1668-1677`),
  which the Tray sends once it starts after logon. The pair owes every class.
  Managed compares only the SID with its last active user, so the same user
  in a new session, as after a logoff and logon, owes nothing new. Its
  Service start applies the machine configuration
  (`ReconcileStartupProfileAsync`); native keeps no machine profile, so the
  console user's activation at the first tick is the start-up application.

The pair is the console session's interactive user. It is the latest
admitted reporter when that reporter reported from the console session, and
otherwise the console user itself (`Runtime::console`,
`thermal_native::console_user`), with the Service's own process recorded as
the steps' caller. Managed's transitions need no report, and by default no
application rule exists, so neither reporter ever sends one. After a switch
to another session, the latest reporter is not the console user.
`console_user` reads the console session's primary token. It classifies the
token's logon with the request path's own `token_context`, and it names only
an interactive one: a token carrying the remote-logon group (S-1-5-14) is
refused, as a remote-interactive request is. The probe names a candidate
only. The attempt's `WindowsGuard::capture` proves it afresh before any read
or write: the same console session unlocked, the same primary SID and logon
LUID, known Windows power and competing-controller absence. The tick reads
the console user at its first pass, after the write epoch moved (every
session notice fences it), and every 3 s (`UNNAMED_POLL`) while no user is
known, so it queries no token on most ticks and still sees a logon without a
further notice. In low power it reads none: no attempt runs there, and the
notice that clears the gate also moves the epoch, so the first tick after it
reads the console user.

The tick takes the mutation gate only when it is free. Otherwise it keeps
its state, updating only its time, for the next tick. An attempt runs
`reconcile_locked` as a report does: the capture, then fresh admission and
health checks before any firmware read (`admit_trigger`, shared with the
report path), then each step's capture, authorization, per-write epoch
check, journal id, readback and stop on ambiguity. In low power the tick
records the cause, renews the lease and takes no capture. The tick's
reconciliation id is a fresh `OpenBlade.LifecycleReconcile` string, and each
step journals under its own `step_request` id as a report's does.

A cause stays pending until an attempt's steps leave nothing owed, a
competing controller drops it, or a newer cause replaces it. The retry
budget is the debt's own, as a report's is:

- A capture refused because fresh competing-controller absence is not
  confirmed drops the cause and settles the pair's debt. `Identity::admits`
  reports that refusal as its own typed error (`competing_controller`), and
  only its controller check returns it. A detection warning or an inventory
  slower than 1 s confirms no absence and is treated the same, as managed
  treats a detection warning as a controller. Strict thermal control stays
  advisory (Architecture rule 5): nothing is queued for after the controller
  leaves, which would overwrite what the user chose in it. Managed matches:
  its power change applies nothing with a controller present and nothing on
  a later unchanged source once it leaves. The next cause or changed report
  owes afresh.
- An attempt refused before any step for any other reason spends nothing.
  Causes include no console user, a refused capture (such as a locked session
  at resume, or a per-user store the native Service cannot open), a fenced
  store, a pending GPU clock confirmation and unknown Windows power. Such an
  attempt reads no firmware and takes at most one capture per epoch move.
  Unrelated notices, such as a battery-level power notice or another session's
  notice, therefore cannot use the cause up before an unlock. A fenced store
  keeps the cause because the fence is OpenBlade's own unsettled journal row or
  a document changed outside the Service, not another controller; a pending GPU
  clock confirmation keeps it for the same reason. A missing per-user store
  directory keeps it because the native Service never creates one: only a
  Service switch, which starts a new runtime, provides it.
- An attempt whose steps leave the debt owed has spent the debt's budget
  through `Reconciled::after`: a deferral spends nothing, a transient
  refusal spends one of `RETRIES`, and every other outcome settles the
  debt. Only a step with nothing possibly submitted can leave it owed. Such
  a step was overtaken by the write epoch, or refused transiently at an
  unmoved epoch, for example by a USB-C adapter whose class is not settled
  yet.
- Either kind is retried only once the ordinary write epoch has moved, as
  an unlock's session notice or a later power notice moves it. Ticks at an
  unmoved epoch take no capture. A retry after steps also waits 2 s
  (`PACE`) after them, and epoch moves within that time only mark the cause
  due, so a burst of notices takes one retry and cannot use up the debt's
  budget before an adapter classifies.
- A fenced store stays fenced across those retries until read-only
  reconciliation settles its row, so a possibly submitted step never runs
  again.

A boundary, low power, or a gap of at least 5 s since the last tick
(`SLEPT`) renews the lease instead of expiring it. Windows `Instant` counts
time asleep, which is not reporter absence, and Modern Standby on this host
can sleep without a suspend notice, so only the gap shows it. A reporter
that stops while the ticks keep running expires as before. The tick stamps
its time after an attempt, so an attempt is no gap. A tick that finds the
gate held stamps it too, so a long request is no gap, unless the gap since
the last stamp already reached `SLEPT`: a request that takes the gate at a
wake does not erase the sleep before it.

`lifecycle-reconcile-vectors.json` pins managed's outcomes, recorded by
`NativeLifecycleReconcileVectors` over the native fixture's saved
performance: lease expiry, a renewed Game report, an unchanged power source,
battery and back to AC, the gate's Enter and Exit around a deferred report
followed by `HandlePowerSourceChangedAsync` on the unchanged source, a
change to battery with Razer Synapse's engine present followed by the same
source once it has gone, and a user's activation on battery followed by the
same user's again. Drift rows add a performance or fan change outside
OpenBlade met by each trigger, an observed HyperBoost, a resume whose
firmware settles within the observation window, and an activation over
drift. Every row records the confirmed fans and `externalChanges`. The resume
row models only that part of managed's resume. It does not run the full
`ProcessNativeAsync` (lighting invalidation and restore, resume validation)
or the later convergence. `request_triggers_lifecycle.rs` replays the rows.

Native tracks confirmed thermal drift as managed does (`thermal_drift.rs`;
managed `ConfirmedStateDriftTracker` and `ResolveAutomaticWriteExclusions`,
`ServiceControlRuntime.cs:16655-16720`). The baseline is in memory, and the
first reading after the Service starts only sets it. A value a reading leaves
unknown is no evidence. OpenBlade's own state is kept per subsystem and only
as a readback confirmed it: a step's or explicit request's readback of its
own write, a fan readback that already shows the request and the
destination it carries, a reconciliation reading that shows the new intent,
and a reading that settles a resume hold. A performance pair written over
manual fans also returns them to Automatic, and its readback confirms both
subsystems (managed `ConfirmedCoupledWrites`,
`ServiceControlRuntime.cs:14932-14938`). An overwrite whose intent has no
destination, such as a GPU clock effect, writes no preset and owns the one
it leaves in place, so a later intent compares with it. Saved intent that
native cannot apply is not OpenBlade's own. A fan intent with no native recipe (Fixed or
a curve on battery or under Silent, battery selectors or Custom, and every
ManualCurve) is projected onto the fans firmware reports, as managed
`ResolveExperimentalFanHardwareDesired` does
(`ServiceControlRuntime.cs:19616-19636`): Automatic fans read as that
intent, and manual fans still mark the change. A setter whose step was
dropped at its bound or deadline, or failed after its intent, has no
readback of its own. The next reading settles it: it is OpenBlade's own
write if that reading shows what was written, and nothing of OpenBlade's
otherwise. Its unsettled journal row still fences writes.

Before any write, an automatic reconciliation compares its fresh reading
with the new intent and with OpenBlade's own confirmed state. A subsystem
matching either is not a change. With no own state for a subsystem, only
the new intent is compared; with neither, as for an intent whose GPU clock
effect leaves it unresolved, the reading is no evidence and the recorded
change stays as it is. Either subsystem changed, or an observed HyperBoost,
excludes both from that reconciliation. The excluded steps write nothing
and reply with managed's paused or deferred message, and the debt settles.
Under manual firmware fans with a fan intent that has no native recipe,
such as Silent with Fixed fans, an unplug and a replug therefore both write
nothing and report `externalChanges` [1] until the fans read Automatic.

A user's startup and a user activation apply over drift (managed
`preserveObservedExternalChanges: false`). Startup is keyed on the SID, as
managed keys activation, so the same user in a new session is not starting
again. Managed ends startup after one transition on classified power and
keeps it pending on unclassified power
(`ServiceControlRuntime.cs:18218-18233`). Native ends it once a starting
reconciliation has compared a fresh reading and a debt of that user then
settles. A debt that settles with nothing compared, such as refusals on an
adapter not classified yet or saved intent that reads no firmware, leaves
the user starting, so its next reconciliation still overwrites instead of
pausing over an intent native never confirmed. An observed HyperBoost that
OpenBlade does not own compares nothing either, so it also keeps startup
pending: the first comparing cause after it still overwrites, including a
change recorded in between, where managed would compare. Startup is not a
debt's own state: only the starting user's reconciliation, or the
activation itself, overwrites.

A capture that a competing controller refuses, at a report, a save or a
lifecycle cause, drops the pair's debt and ends startup
(`Runtime::drop_for_controller`). This is native's stricter write
admission, not managed parity: managed's `ApplyFullTransitionAsync` and
`ScopedThermalWriteBlockReason` have no controller clause, so its startup
under a controller writes the saved profile. A controller-detection
failure, or an inventory slower than 1 s, returns the same denial with no
controller present. With nothing of OpenBlade's own confirmed, the next
power change then compares only its new intent. Unless firmware already
shows it, the user sees `externalChanges` [0] although nothing changed
outside OpenBlade. That heals when a later intent matches firmware;
otherwise it stays paused until an explicit request. Only captures drop a
debt this way: a step whose own authorization a controller refuses inside
`reconcile_locked` spends a retry instead.

A power change within one minute (`RESUME_WINDOW`) after the last gate
boundary, in low power or after the resume, is a resume change. When its
reading matches neither the new intent nor OpenBlade's own state, and
OpenBlade has own state to compare, the change is recorded and held for one
minute from that reconciliation (managed `PendingResumePerformanceDrift`,
`ServiceControlRuntime.cs:19029-19087`). Only a fresh device reading clears
it, and only one that shows the held destination within the window: a
published status read settles nothing, and a fresh reading of anything
else leaves the change recorded. The hold compares no competing-controller
evidence; every write keeps its own admission. The next boundary, a power
change, low power, a reconciliation toward another intent, or OpenBlade's
own write ends the hold. The status publishes `externalChanges` in managed's
order. Exclusion only removes writes: every remaining write keeps its
exact-device, competing-controller, power, session and readback admission
and stops on ambiguity.

Native differs from managed in these ways:

- Convergence. Managed's convergence loop reconverges confirmed drift under
  MaintainDesiredState (above). Without `service-thermal-convergence-v1`
  native has none, so a subsystem stays excluded until a reconciliation
  reads OpenBlade's intent again, an explicit request writes it, or the
  Service restarts. With it, the loop reconverges it
  ([Convergence loop](#convergence-loop)).
- Own state at a report and at lease expiry. Managed publishes the new
  desired state before the transition reads it as the prior one, so a
  report that leaves a rule (`otherReportedWithOwnPreset`) and the lease
  expiry (`leaseExpired`) both pause over OpenBlade's own preset, and
  managed's convergence loop repairs that later. Native compares OpenBlade's
  own confirmed state and applies at once. This is a permanent, declared
  divergence, kept when native gains its convergence loop: managed's repair
  spends the one convergence claim for that destination and policy revision
  on OpenBlade's own preset, and under Strict, where nothing converges, the
  pause strands OpenBlade's own Fixed fans until an explicit reapply. The
  vectors keep managed's rows, and
  `a_report_over_its_own_preset_applies_where_managed_pauses` and
  `an_expired_lease_applies_the_power_profile_once` pin native's outcome.
  A reconciliation deferred or retried before any submission keeps its
  prior state, as managed's `PendingTransitionContinuation.PriorDesired`
  does, and a destination one confirms before a later step is retried is
  OpenBlade's own from that readback.
- Narrower readings. Native reads the selector pair and the manual-fan bit:
  fans compare as manual or Automatic, so Stop and Fixed both read as
  manual, and GPU clock offsets are no evidence. HyperBoost unknown has no
  native reading; an undecodable reading resolves nothing and writes
  nothing. A competing controller refuses the capture before any reading,
  so native records no external change there where managed does
  (`powerToBatteryWithController`, whose [0] is drift against the desired
  state managed published first, not a controller gate). An excluded
  step's own reading reaches
  `confirmedState` only at the next status reading.
- Resume on an unchanged source after a deferral. Managed's
  `HandlePowerSourceChangedAsync` submits nothing then; its convergence loop
  reconverges later under MaintainDesiredState. Native applies the deferred
  debt at the tick after the boundary.
- Activation timing. Managed activates a user at its first admitted request.
  Native activates the console user at the first tick that reads it, and
  only the console session's user, whose capture must prove it unlocked.
- Controller and save admission. Managed's startup and ordinary thermal
  transitions have no competing-controller clause; its controller gates
  cover fan-curve maintenance, fan recovery and convergence only. Native
  refuses every write while fresh controller absence is unconfirmed, which
  drops the cause and pauses the next power change as described above.
  Managed also stores profiles without device, power or controller
  admission, while a native save outside low power takes the write capture
  and is refused under a competing controller, a locked session or an
  unknown AC status. If saves are relaxed later, a controller refusal of a
  step's own authorization inside `reconcile_locked` does not take
  `Runtime::drop_for_controller`, so it needs its own handling.

Offline evidence covers the tick's logic through injected probes only. The
native wiring is compile-only, and no offline test reaches it:
`Runtime::native`'s start-up power-kind sample, the
`thermal_native::console_user` probe with its token classification, and
`WindowsGuard::capture`'s typed competing-controller refusal, which the rig
models with its own flag. They need a physical logon, lock, unlock,
Synapse and sleep/resume validation.

Remaining residuals:

- A cause refused before its steps for a reason other than a competing
  controller is retried at every epoch move for as long as it stays
  refused, one capture each time, until a newer cause replaces it. There is
  no separate ceiling, since such a refusal reads no firmware.
- A controller that appears after the attempt's capture refuses a step's
  own authorization instead. That step spends one of the debt's `RETRIES`,
  and the next attempt's capture drops the cause if the controller is still
  present.
- Steps from the console pair record the Service's own process as their
  caller.
- The first tick after the Service reports RUNNING can write. The readiness
  self-probe's status read creates the status feed's publisher before the
  serving loop starts (`readiness.rs` `Ports::probe`, `StatusSource`
  `observe_since`), so no client is needed, and that tick activates the
  console user. Managed also writes at start
  (`ReconcileStartupProfileAsync`).
- The gap heuristic does not help if the Service keeps ticking through
  standby.

Not ported:

- Managed's display-off work (`ReportConsoleDisplayState`,
  `PrepareOwnedHyperBoostForDisplayOffAsync` and lighting suppression).
  Native composes no HyperBoost or lighting renderer, and display-off never
  sets the gate in either.
- A swap between the classified external classes, full and limited adapter,
  under an unchanged Windows AC line status. Detecting it needs a firmware
  adapter read on a notice and a record of the last applied class; the next
  report, or a step's own adapter reading, finds it instead.
- Sessions other than the console's. A disconnected session's debt is kept
  for its next report or for when it is the console session again.

#### Thermal control policy and convergence status

With `service-profile-triggers-v1`, native stores the thermal control policy
and serves the convergence status member. Without
`service-thermal-convergence-v1` it has no convergence loop, so neither policy
adds a write.

The policy and its upgrade disclosure are the managed preferences members
`thermalControlPolicy` (Maintain desired state 0, the default, or Strict 1)
and `thermalPolicyUpgradeDisclosure` (not required 0, pending 1,
acknowledged 2). Migration below schema 16 stores Maintain with the
disclosure pending, as `BatteryConfigurationStore.ReadPreferences` does, and
keeps both from schema 16 on. `Storage::thermal_policy` reads them and
`Storage::save_thermal_policy` changes them. A preserved aggregate, or a
configuration this service failed to replace or saw change, is quarantined
(`configuration_quarantined`), as managed quarantines its configuration. A
thermal-journal or battery fence leaves the configuration admitted: the
policy reads as saved and is pending reconciliation, and a change is stored.

`setThermalControlPolicy` and `acknowledgeThermalPolicyUpgradeDisclosure`
(`thermal_policy_mutation.rs`) follow managed `SetThermalControlPolicyAsync`
and `AcknowledgeThermalPolicyUpgradeDisclosureAsync`. The request frame is
validated strictly: a policy other than 0 or 1, a non-integer, a missing or
extra member, or another version closes the request without a change. A
change that leaves the preferences as they are writes nothing. Otherwise the
preferences and the saved profiles are committed as one aggregate through the
recorded transaction above, after the staged documents reopen through this
service's reader, and the reply follows the readback. A failed write that is
restored replies with the restored text, one whose restore cannot be
confirmed quarantines the aggregate, and a quarantined configuration refuses
both requests with managed's recovery texts. Each request runs under the
thermal recipe budget (`mutation_budget`), since it waits for the operation
gate a thermal request holds. No IPC protocol member changes.

`thermalConvergence` (`service_status/thermal_convergence.rs`) is published
in the owner's private projection only; other views skip the store reads.
`thermal_mutation::convergence` derives it at each publication from the
saved policy, the admission facts managed's evaluation reads, and the
confirmed state against saved intent, in managed's order
(`ResolveThermalConvergenceClosureReason`, then
`ThermalConvergenceClassifier`):

| Condition | State | Reason |
| --- | --- | --- |
| Configuration quarantined | Paused | ConfigurationQuarantined |
| Strict | Paused | StrictPolicy |
| Maintain with the disclosure pending | Paused | UpgradeDisclosurePending |
| Low power, or the lifecycle unreadable | Paused | LowPower |
| No settled power class | Paused | PowerUnsettled |
| A possible competing controller named | Paused | CompetingControllerPresent |
| A thermal write not settled, or a journal or battery fence | Paused | PendingReconciliation |
| HyperBoost confirmed active | External, both components | HyperBoostActive |
| A contradiction managed would converge | Paused | InternalStateUnavailable |
| Manual fans on both sides, not OpenBlade's own | Paused, Fans, explicit reapply | ManualFanReadbackAmbiguous |
| A reading or saved intent native cannot resolve | AwaitingObservation | AwaitingFreshObservation |
| Confirmed state matches saved intent | Matched | None |

The projection that carries the member needs a write capture, which needs
the lifecycle's ordinary epoch (`WindowsGuard::capture`). So the LowPower
row follows managed's order but is not published natively: the member is
absent instead (below). Low power is
read from that epoch (`Lifecycle::active`), never from the gate probe, which
belongs to the reconciliation and whose checks the trigger tests count.
Managed's CompetingControllerUncertain reason has no row: the fresh capture
the projection takes has already confirmed controller absence, or refused.
Manual fans are OpenBlade's own only when this user's exact fan write was
confirmed and nothing changed since (`thermal_drift`).

Status reports any external power as the coarse class 3, and the status's
own saved intent stays at that class. For the member only, under AC
presence, the class this user's own last step classified from its adapter
reading (`Storage::observe_power`) stands in for it while the Windows power
generation is unchanged (`convergence_power`). So the member classifies on
PluggedIn and USB-C as that step read them, and a fan request's own
publication and the projection it retains agree. Before any such step, and
after a change of the Windows power kind, AC is PowerUnsettled. A swap
between full and limited adapters under unchanged AC presence is not seen
until the next step. Full parity needs managed's periodic 07/8C adapter
read in the status read job, a separate slice.

Native differs from managed in these ways:

- No loop. Where managed publishes Converging with the affected components,
  native publishes Paused with `InternalStateUnavailable`, and the drift
  stays excluded as described above. Managed's Converging, Suppressed,
  RestorePending and ReconciliationUnavailable states, and its budget,
  tombstone and HyperBoost-journal reasons, need that loop and are never
  published. Strict stays advisory either way.
- Power. AC is classified only from this user's last step, as described
  above; managed reads the adapter itself.
- Absent member. The member is absent, where managed publishes a Paused
  row, whenever the projection's write capture is refused or no longer
  current: in low power, under a locked session, an unknown AC line, a
  competing controller or a fence held by another user, and for a
  preserved aggregate, since the saved-intent reads before it fail.
  The projection a fan request retains pins its capture's epoch, so it is
  absent from the next power or session notice until another request
  retains a projection. The member is also absent until a thermal request
  or startup retains this user's policy.
- AwaitingObservation. Managed publishes it after a policy change until its
  next observation; native derives what that observation would give from
  the confirmed state the status already carries.
- Admission. A native change takes the request capture as a profile save
  does, so a competing controller, a locked session or an unknown AC status
  refuses it with the saved-configuration text, and in low power the
  storage capture stores it; managed admits the active session and an
  admitted configuration only. A caller outside the control session is
  closed without a reply, where managed replies Blocked; a SYSTEM caller
  receives the session text. A store failure, including a store read
  before the transaction, replies Failed with the store's reason, where
  managed's exception ends the request without a result; fatal evidence
  still ends the Service.

`tests/data/thermal-convergence-vectors.json` holds managed's statuses and
replies, recorded by
`ServiceControlRuntimeTests.NativeThermalConvergenceVectors.cs`.
`thermal_convergence_tests.rs` replays each status through the strict codec
and checks that native classifies the same components where managed
converges. `readiness_tests/thermal_policy.rs` replays the replies through
the framed dispatcher. `tests/data/profile-migration-vectors.json` carries
managed's Strict and Acknowledged rows that survive migration.

#### Shared convergence ledger

With `service-thermal-convergence-v1`, which implies the lifecycle triggers,
the store also reads the user's convergence ledger. Native shares managed's
`thermal-convergence.json` (`ThermalConvergenceStateStore`): the same
per-user directory, schema 1 and members. `openblade_domain::thermal_convergence`
ports the document, its validation and managed's ledger transitions
(`ThermalConvergenceLedgerTransitions`: refresh, reserve, complete), and the
revision hash byte for byte: SHA-256 of the default PascalCase serialization,
read as a .NET `Guid`. The feature has its own package marker and both
package readers refuse it by feature and by marker, so no package carries it
yet.

`tests/data/thermal-convergence-ledger-vectors.json` holds the revisions,
documents and transition states, recorded through managed's own transitions
and store by `NativeThermalConvergenceLedgerVectorsTests`. Its native rows are
documents the Rust encoder wrote, which managed loads.
`thermal_convergence_vectors.rs` replays all four sections.

The store reads the ledger once, when it opens, and fails closed
(`thermal_convergence_ledger.rs`). A file that is unreadable, unparsable,
duplicate-keyed, of another user, invalid or of a newer schema closes
convergence for this user, and the store never writes over it. So does any
write it cannot confirm, since a reservation that may be durable must never
be mistaken for one that is not. A closed ledger stays closed until the store
opens again, at a user switch or a Service restart, as managed reloads it
only at activation. A write is a compare-and-swap on the bytes the store
holds, through its own atomic `Files::replace`, so a ledger changed outside
the Service is never overwritten. An empty state is never written, as managed
never persists one.

The status reads the ledger at each publication:

| Condition | State | Reason |
| --- | --- | --- |
| Ledger not authoritative, after a quarantined configuration | Paused, policy published as Strict with the saved disclosure | ConfigurationQuarantined |
| A reservation, power settled and not low power, under either policy | AwaitingObservation, its declared components | AwaitingFreshObservation |

A reservation comes before the Strict, disclosure, controller and pending
rows, as managed reconciles one whatever the policy
(`ThermalConvergenceReconciliationAdmitted`).

Native differs from managed in these ways:

- Revisions. Native's intent comes from its own resolution: Custom
  performance never carries `maximumFans`, and GPU clock tuning is never
  set. Where that differs from managed's `ResolveHardwareDesired`, the
  revisions differ, so a switch between the managed and native Service
  starts a new budget generation once, at most.
- Record equality. Managed compares the ledger's lists by reference, so a
  loaded tombstone never equals a new one; native compares them by value.

#### Convergence loop

`thermal_convergence_loop.rs` ports managed's observer and executor
(`EvaluateThermalConvergenceObservationAsync`,
`ExecuteThermalConvergenceAsync`). It runs after the lifecycle tick, under
the mutation gate, and evaluates each reading the mailbox published once, as
managed evaluates an observation. A reconciliation step's own baseline read
or readback is no evidence, so a status that publishes a step's snapshot is
not evaluated again. Every 15 s while Maintain is admitted the loop asks for
its own reading through the same admitted read path, with no optional
reading (D5); Strict reads nothing. That read is bounded by its deadline,
and a failed or late read is no reading: the tick goes on with its low-power
and lifecycle work. A due D7 retry asks for a reading once per 5 s until one
is evaluated. A tick with none of these reads no console user and captures
nothing.

An evaluation needs an authoritative ledger, the console user's settled pair
with no lifecycle cause owed or pending, no low power, settled power, no possible
competing controller, no unsettled journal row, Maintain with no pending
disclosure, and a safe HyperBoost journal. Managed's journal,
`ProgramData\OpenBlade\hyperboost-ownership.json`, is read read-only through
the protected path checks. It is safe when it is absent, or Relinquished in a
schema managed recognizes with the members that schema requires. Anything
else, including a read that fails, publishes RestorePending with
`HyperBoostPending` and spends no claim (D8). While the user is starting or
a lifecycle cause is owed or pending, the status shows Awaiting with
`AwaitingFreshObservation` rather than Converging, since the lifecycle's
reconciliation runs first. Each status projection reads the journal again,
a small protected-path read, so the status never shows a stale journal.

A contradiction refreshes the ledger to current intent. Its tombstone or a
spent budget suppresses it; otherwise it reserves one claim, persisted before
any setter. The attempt runs the lifecycle's typed steps for the
contradicting subsystems only, overwriting drift. Each step's journaled
intent is admitted afresh: the ledger still holds the reservation, the
HyperBoost journal is unchanged and safe, the policy is still Maintain and
the Service is not stopping. Once one write is refused, no later write of the
attempt is admitted.

| Attempt outcome | Ledger |
| --- | --- |
| Every step confirmed by readback | Tombstoned Confirmed |
| A possible submission whose journal row still fences the store | Tombstoned ReconciliationUnavailable |
| Every submission settled, and the last step was refused before submitting | Tombstoned Blocked |
| Every submission settled, and a readback showed another state | Tombstoned External |
| Refused before any submission, for a cause that may clear | Reserved for one retry (D7) |
| Any other refusal, or a retry refused or changed | Tombstoned Blocked |
| Stop during the attempt | Reserved, reconciled read-only |

A reservation this runtime did not make, or one a stop left, is reconciled
read-only from the next reading: a match retires it Confirmed and a confirmed
contradiction External. An unknown HyperBoost state or an incomplete reading
keeps it. A journal row that still fences the store keeps it too, also after
a restart: the reservation stays until that row settles, its claim stays
spent, and nothing is replayed.

Native differs from managed in these ways:

- D7. A reservation whose steps were all refused before any submission is
  kept and retried once, at least 5 s later, from a fresh reading under fresh
  admission, with the claim it already spent. If the contradiction, intent,
  policy or destination changed, it is tombstoned (Confirmed when the
  reading now matches, Blocked otherwise), and so is a retry refused again.
  Managed attempts once.
- D9. A contradiction native has no recipe for is refused before reserving,
  with no claim spent: for performance, no saved destination, an incomplete
  GPU clock policy or an owned fan change still to hand back; for fans, fan
  intent native has no recipe for (ManualCurve, Stop, or Fixed and Maximum on
  battery or under a destination other than Balanced or Performance) or an
  owned fan row no live owner holds; for either, no saved fan intent. Before
  reserving, a fan contradiction over an owned fan row also needs the
  continuity the fan step itself checks (`Runtime::holds_fan_continuity`:
  the live owner, its proof generation, custody and authentication), so the
  attempt is never reserved only for its fan step to refuse it. Managed
  projects unsupported fan intent onto a confirmed Automatic reading, and so
  does native, so such intent contradicts nothing then and a performance
  drift still converges. A return to Automatic carries the saved preset in
  its 0D/02 pair, as managed's fan setter carries a performance selector, so
  a fans-only attempt may write that preset.
- The no-settle ambiguity. Native's steps cannot tell a possible submission
  that did not settle by readback any further than its journal row does:
  ReconciliationUnavailable while the row fences the store. Once every
  submission settled, the attempt takes its last step's disposition, as
  managed's does. Nothing is replayed, by the running Service or a restarted
  one.
- A reservation whose persistence fails closes the ledger, so the status
  reports `ConfigurationQuarantined` where managed reports
  `InternalStateUnavailable`.
- Native does not track a device reconnect, so its refresh never invalidates
  the device authority; a changed model is still a new one.

`readiness_tests/thermal_convergence_loop.rs` drives the loop over the rig's
store, lifecycle and scripted device: a drift converged once and its tombstone
holding; Strict, an unsafe and an unreadable journal; a journal change, a
competing controller and Strict arriving between two writes; Stop before and
after a submission with a restart; a suspend and a lock refusing an attempt,
then its retry, including one an external change resolved; an ambiguous
submission and a restart with its row outstanding; a spent budget; and a
damaged and a newer ledger, running and restarted. It also pins the review
fixes: ManualCurve on AC and Fixed on battery projected onto Automatic while
a performance drift converges; no evaluation from a reconciliation's own
readings; the console probe, captures and frames over idle ticks under
Strict and Maintain; a due retry asking once per interval, a retry refused
again, and Strict retiring one unread; an unsettled row, a refused admission,
a ledger changed between writes and one refusal refusing every later step;
the fan step's convergence, mid-step refusal and Stop; the drain stopping
the loop; a left reservation retired Confirmed; a tombstone holding without
its budget; a failed and a stalled periodic read; an owned fan row a crash
left without its live owner spending no claim; a step's own read retiring a
publication as evidence; and a last step refused after a confirmed one
retiring Blocked.

#### Explicit reapply

A tombstone or a spent budget suppresses its contradiction, and the status
publishes Suppressed with its components, `Tombstoned` or `BudgetExhausted`
and `explicitReapplyRequired`, as managed does. With
`service-thermal-convergence-v1`, native serves managed's
`reapplyChangedSettings` at protocol 90 (`thermal_reapply.rs`), its only
exit. It needs no wire change: the request carries only its version and
request ID, and the reply is an ordinary operation result with the status
the request published.

Under the operation gate, in managed's order (`ReapplyChangedSettingsAsync`):

| Condition | Reply | Ledger |
| --- | --- | --- |
| An unsettled journal row (managed's pending write confirmation) | Blocked, confirm the current laptop state | Kept |
| A reservation in the ledger | Blocked, reconcile the reserved transition | Kept |
| Tombstones or budgets, and the replacement fails | Blocked, the suppression could not be retired | Kept; a failed write closes the store |
| Tombstones or budgets | Rearmed, then the rows below | Tombstones, budgets and the terminal result cleared |
| No drift | Stored, no settings changed outside OpenBlade | |
| Drift | Applied, or Blocked or Failed with what is still changed | |

The rearm is a compare-and-swap on the bytes the store holds
(`Storage::replace_convergence_ledger`), so a ledger the store no longer
holds as read, or one the encoder refuses, is kept without closing anything.
A failed write, including one refused because the file changed under the
running Service, closes the store, which the status then reports
quarantined. Once a rearm is attempted, the status
is republished from the ledger at once, before any step runs, as managed
publishes `AwaitingObservation` right after its rearm, so a request a client
disconnect drops later still leaves the rearmed status published. After a
rearm the next evaluation of the same drift reserves a fresh claim.
The drift reapply is one reconciliation scoped to the drifted
subsystems native tracks, performance and fans, ordered as managed's
transition order and overwriting the drift. Each step keeps its own capture,
write admission, readback and stop-on-ambiguity path, and spends no claim.
Manual fans on both sides of a performance reapply block both, with
managed's per-subsystem reasons. The reply names what was reapplied and
what is still changed, with each remaining subsystem's reason.

Native differs from managed in these ways:

- A caller outside the control session is closed without a reply, as for
  every mutation, where managed replies Blocked.
- The request capture precedes the pending and ledger checks, so a
  competing controller, a locked session or an unknown AC status refuses the
  request before them, as it refuses a policy change.
- Unknown Windows power blocks with managed's unclassified-adapter reply.
  An adapter only a step's own reading finds unclassified is that step's
  refusal.
- A power class changed since the last reconciliation is the lifecycle
  tick's, so no full transition runs first, and native has no automatic
  fan-stop arming to pause.
- The manual-fan bridge is stricter than managed's. Native counts an
  outstanding fan row as manual fans and treats saved manual fans at either
  AC class as manual, because the step's own adapter reading is what tells
  the two classes apart. Managed reads the confirmed fans and the class it
  resolved. Native therefore blocks every performance reapply managed
  blocks, and may block one managed would apply.

`tests/data/thermal-reapply-vectors.json` holds managed's replies, the
ledger each leaves and whether it wrote, recorded by
`ServiceControlRuntimeTests.NativeThermalReapplyVectors.cs`.
`readiness_tests/thermal_reapply.rs` replays each row through the framed
dispatcher over the convergence rig, and pins a rearm followed by the same
drift converging again with a fresh claim, a rearm that cannot persist and
its immediate quarantined publication, the pending and in-flight blocks
writing nothing, a rearm still published when a client disconnect drops the
request at its first step, a Stored reply after a rearm, unknown Windows
power, a performance-only reapply running no fan step, and the manual-fan
bridge on battery. `thermal_reapply.rs` checks the three reply summaries.

## Fan Stop without its guardian (`service-fan-stop-v1`)

Managed's production composition wires `GuardianFanStopBackend` over
`WindowsFanStopGuardianClient`. When that client cannot verify the guardian it
reports `GuardianUnavailable` with no message of its own, so
`FanStopController` publishes Unavailable and refuses Stop and Automatic when
its startup disposition is Clear (see the first deviation below). The
`service-fan-stop-v1` feature serves the four Fan Stop requests as managed
does in that state. It implies `service-profiles-v1`, adds the
`service-fan-stop-v1` capability marker, and both package readers refuse it
by feature and by marker (`native_package.rs`, `WindowsNativePackage.cs`), so
no packaged composition implies it. Nothing in it touches hardware: the
guardian, its EC mailbox and its ownership lease are planned separately.

- `setFanStopMode` replies `Blocked` with the published status message for
  both modes: the unavailable message on exact RZ09-0581, and the
  no-evidence message without it. Automatic first pauses automatic Stop
  (`ManualAuto`), as managed's `SetFanStopModeAsync` does before it
  dispatches. Managed's source answers Automatic with `Blocked`, not with a
  stored "no ownership" reply; the vectors record that.
- `resumeAutomaticFanStop` replies managed's `Blocked` refusal.
- `setFanStopSettings` takes managed's codec checks
  (`FanStopSettings.IsWellFormed`, members optional over managed's
  initialisers, unmapped members refused), then managed's session and
  configuration checks, then stores the settings in the preferences
  document through the profile store's aggregate transaction
  (`Storage::save_fan_stop_settings`). Equal settings are not written
  ("already saved"). Saving them disables, arms or leaves automatic Stop as
  managed's eligibility does.
- `setBatterySaverFanStop` refuses a preserved configuration first, then
  enabling, because RZ09-0581 has no Battery Saver fan stop
  (`SupportsBatterySaverFanStop` false). Turning it off stores the Battery
  profile's `stopFansInBatterySaver` with the committed preference bytes,
  only when it changes (`Storage::save_battery_saver_fan_stop`).

Both saves share `Storage::commit` with `save_profiles` and the thermal
policy save, so a failed write restores the prior aggregate, a stage that
cannot be removed refuses the save without writing, an uncleared record
quarantines the store, and a restart reads only what is durable
(`profile_storage_tests.rs`, `fan_stop`).

On exact RZ09-0581 host metadata the status publishes managed's
`fanStopStatus` with reason `GuardianUnavailable` and the unavailable
message, and the observation record carries `hasFanStopTrialEvidence` and
`hasFanStopSourceEvidence`. `supportsFanStop` stays false. A composition
without the feature keeps the no-evidence status and both flags false.

`ServiceControlRuntimeTests.NativeFanStopVectors` records managed's replies
and statuses for each case through the real runtime and the v90 codec into
`tests/data/fan-stop-vectors.json`, and the evidence flags from the
production factory's own predicates. The native readiness tests replay every
row through the framed dispatcher (`readiness_tests/fan_stop.rs`) and the
capability tests compare the record in every lane.

Deviations from managed:

- Native never reads the `FanStopGuardian` store. Managed computes
  `StartupDisposition = store.InspectStartup()` even when the guardian is
  unavailable (`WindowsFanStopGuardianClient.cs`): a
  `<machineData>\FanStopGuardian\pending.json` makes it Unresolved, and an
  inspection that throws makes it Unknown (`FanStopGuardianTransport.cs`). A
  non-Clear disposition makes managed publish RecoveryRequired ("Fan-stop
  startup ownership is unresolved; commands remain fenced."), reply Blocked
  to `setFanStopMode`, and fence ordinary hardware writes
  (`GuardianFanStopBackend.cs`; `ServiceControlRuntime.FanStop.cs`). Native
  replies as for a Clear startup in every case. This is latent while both
  package readers refuse the feature. The reservation read, the
  RecoveryRequired publication and the write fence belong to the Rust
  guardian work (option A) and must land before `service-fan-stop-v1` can
  ship.
- A caller without the control session while the configuration is
  quarantined: managed checks `ConfigurationIsAdmitted` first
  (`ServiceControlRuntime.cs`) and native checks the session first
  (`fan_stop_mutation.rs`). Only the reply message differs.

- Evidence comes from the matched host metadata (`RZ09-0581`), not from a
  device observation, and the observation record is published only beside
  a confirmed active HyperBoost, as every native record is.
- The caller's saved settings and automation state are projected only after
  that caller's own Fan Stop or thermal request has captured the store.
  Before then, and in another user's view, the status shows the managed
  defaults. `batterySaverFanStopStatus` stays absent.
- Managed also pauses armed automatic Stop at mode, power and lifecycle
  boundaries. Nothing can arm Stop entry here, so those pauses are not
  tracked; they would change only the published pause reason.
- A caller outside the control session is closed without a reply, where
  managed answers its session refusal. A store failure replies `Failed` with
  the store's reason, where managed's exception ends the request without a
  result. A refused request capture (competing controller, locked session,
  unknown AC status) replies `Blocked` with the capture reason, where managed
  stores the settings.
- A preserved aggregate found while serving answers both saves with
  managed's preserved-configuration replies. Managed's first save over an
  aggregate that changed since it loaded instead raises its quarantine
  exception and pauses automatic Stop for a safety condition.
- The settings decoder is stricter than managed's web defaults: member names
  must be exact camelCase and numbers must be JSON numbers.

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
submitted." (no fan setter, for a fan request). Owned Stop fan cleanup is
paced once, before its first row (`Pace::covers_before_rows`), against what is
left of the source drain: with no append timed yet, every row left, its 31
included, is charged at the second longest exchange, so one stall still counts
once. When the recipe does not fit it writes no row and sends no setter. The refused recipe's transaction IDs were reserved but never sent, so
retirement returns them to the pool. A fan record pins its IDs only once a
setter may reach the device, so a refused fan recipe returns them too.

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
connection after a commit. A step whose transfer has not retired
`RECONCILE_MARGIN` (1 s) after the deadline is dropped exactly as
`serve_mutation` drops an explicit request at its deadline, and the trigger
answers and releases the mutation gate. The Reader keeps that step's worker
and permit, so every later mutation first waits for the worker's own
retirement and then settles the permit (`ReadSource::drain_mutation`), and a
journal row past its intent stays unsettled and fences the store. No later
request can submit while the old worker still might. The reply is Failed "The
reconciliation did not finish within its budget." with the store's
reconciliation reason, after a save too ("Profiles were stored. ..."): the
profiles are stored and a command may have been submitted, so the caller must
not read Stored as a settled hardware result. When the step had journaled
nothing that could be submitted, it is a transient Blocked refusal instead.
A step whose newest row is Prepared, with no setter intent yet, counts as
possibly submitted at the bound: the reply is Failed and the debt is settled.
That is conservative, since the worker may still be running and the journal
alone tells which.
The native Agent's report waits for the budget and one frame more (13 s,
`REPORT_REPLY_BUDGET`), which covers the margin. The managed tray's
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
drain; uncertain publication retains the fence. Every mutation entry point stops
the reader before its own admission checks: `ReadSource::mutate`, owned-Stop
`mutate_cleanup`, and `drain_active`, which the battery, temporary-charge and fan
entry points (a reconciliation's fan step included) call before they capture
admission. Each stop retains the reader-drain phase before it waits
(`ReadSource::stop_reader`), and the next read or mutation rebuilds the reader
only after the stop is confirmed and integrity and read custody are freshly
admitted. A refusal that submitted nothing therefore leaves reads running, even
when its caller returns without calling `mutate`, the drain was canceled, or the
request was refused while suspended and the system has resumed. A fenced source,
an unconfirmed mutation-worker or reader stop and an integrity failure stay
sealed. Initial read admission precedes
ID reservation. A sent ID returns to the pool only after a later answer to the
same command; see the allocator rules in
[the native development Service](windows-rust-development-service.md).

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

The read-parity gap for observed HyperBoost is closed in the status: a status
whose confirmed state has an active HyperBoost carries the observation
capability record, which the managed decoder requires there (see
[live readings](windows-rust-development-service.md#live-readings-read-only)).
Every other status keeps null capabilities. The record admits no control. An explicit Automatic fan
request still stays refused while HyperBoost is observed; lifting that refusal
is a separate change. Owned Stop, which publishes nothing, closes the owner with 51.
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
not executed. Managed tests parse emitted native replies with the actual v90 codec
and inspect explicit artifact capabilities and conservative recovery refusal.
The native test marker emits the original received UTF-8 payload, preserving
property order and the leading polymorphic discriminator. Extraction retains
those payload strings unchanged; parsed/sorted JSON is not wire evidence.

Final evidence must bind the exact clean commit/tree, full compiler inputs and
outputs, actual feature/define sets, complete inspected test bodies, held harness
hashes, exact prelists and original logs/TRXs. Prior evidence is reusable only for explicitly unchanged inputs with recorded
equality; changed production paths require fresh focused evidence. Installation, hardware validation, lifecycle
experiments, package publication and default activation remain outside this stage.
