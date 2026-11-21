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
SCM power/session ingress advances a separate lifecycle epoch and fences old
operations while preserving loaded-origin custody. Only STOP/SHUTDOWN or actual
trust loss is terminal. Suspend blocks mutation until an explicit resume; session
events cannot establish resume. Fresh exact WTSInfoEx active/unlocked session
admission is required for mutation. The initial clear suspend latch follows the
managed lifecycle contract; display-off alone is not confirmed low power.

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

On classified power that agrees with Windows, managed
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

A class that disagrees with Windows is not managed's source: this check does
not apply, and the recipe's power fence submits no setter.

Managed leaves confirmed HyperBoost for a battery preset through the
`hyperboost-restore-balanced` pair first (`CanExitHyperBoostDirectlyTo`; the
RZ09-0581 `HyperBoostBaseModeAdmissionSource` admits direct exits to Silent,
Balanced, Performance and Custom only). That pair belongs to the separate
HyperBoost fixture and is not admitted natively, so native stores the intent
and replies Blocked without preparing a row (`Plan::NoDirectPath`). A direct
exit to Silent keeps the first reading's classified external class, as the
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
Custom (managed `ApplicationProfileResolution`). Managed fills missing levels
from the confirmed state or Medium/High; native selects no destination then.

`select` in the installed thermal runtime resolves the saved destination for a
fresh class without storing anything; a later automatic reconciliation of saved
profile intent can reuse it.

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
hardware authority. Reports only select intent; this slice has no automatic
profile reconciliation. It can use the existing managed reporter wire operation;
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
| Unclassified 07/8C reading without fresh Windows AC presence, or with confirmed HyperBoost | Blocked, `Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked.` |

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

Managed is the reference, and four shared vector files under
`tools/openblade-windows-host/tests/data` pin it:
`profile-set-vectors-v27.json` (decode, validation, reply and stored bytes),
`profile-aggregate-vectors.json` (loading and migrating schemas 1 to 27),
`profile-ordinal-ignore-case.json` (the comparison that finds duplicates) and
`profile-capabilities-rz09-0581.json`. The managed tests
`ServiceControlRuntimeTests.NativeProfile*` record every expectation from the
IPC codec, the production validator, `UpdateProfilesAsync` and
`BatteryConfigurationStore.LoadAsync`, and verify the same files the Rust
tests read. To regenerate after a managed change, set
`OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS` to that data directory and run
those tests.

Differences from managed:

- No hardware application. Managed restores lighting it had suppressed and,
  when an edit changes the active application profile's intent, runs a full
  transition. Native has no automatic application or reconciliation yet, so a
  stored set is always answered as stored.
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

Not done here: the read path still refuses a committed schema 24 or older
aggregate and the schema-less legacy profile before any request is served, so
the migration this stage can perform is reachable for schemas 25 and 26 and
for an absent aggregate, which is materialised from the safe defaults on the
first save.

Packaging refuses this composition. `build.rs` gives a build with
`service-profiles-v1` the temporary-charge capabilities followed by
`service-profiles-v1`, and both package readers (`native_package.rs`,
`WindowsNativePackage.RequireCapabilities`) refuse that feature and that
capability by name, whatever else a record claims. A profile-serving image
cannot be packaged, bound or installed, as the temporary-charge composition it
extends or as anything else. Tests on both sides pin the refusal. Admitting it belongs to the packaging stage.

### Active application profile

Resolution of the active application profile is ported, write-free, and not
yet called by the installed Service; a later slice wires it to saves,
foreground reports, lease expiry and power changes.
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
caller that changes rules without `Lease::update` gets no overrides. The
installed Service does not use them yet: `thermal_storage.rs` selects the
application override with four stateless matchers (lines 584-594, 643-653,
826-836 and 988-998) that take the first enabled rule for the reported path on
every read. After a save that gives the active rule's path to a new id, managed
keeps no profile until the next report, while those matchers select the new
rule at once. The wiring slice replaces them.

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
deadline after the 275 ms of spacing. Before its first journal row each
operation also checks its own measured pace (`Pace`): the mean time per exchange
so far, storage and admission included, times the exchanges left plus the fixed
spacing must end by the deadline. Otherwise the intent stays saved, nothing is
prepared or submitted, and the reply is Blocked with "The remaining request time
cannot cover this recipe at the measured device pace; no thermal setter was
submitted." (no fan setter, for a fan request).

A fan operation's checkpoints reread saved policy, so that a change between
setters stops it. They resolve it again only when a digest of its inputs
changed: the override and configuration files, the override bytes this storage
accepted and both temporaries (`Storage::policy_inputs`). A full resolution
parses the aggregate up to four times (about 226 us in memory, before file
access), the digest reads the five files once (about 1.2 us in memory).
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
Confirmed (ordinals 0-5), plus KnownNotSubmitted (6). Every journal owner nonce is checked against the complete validated
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
