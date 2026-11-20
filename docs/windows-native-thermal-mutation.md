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

Manual overrides at schemas 8, 9 and the current 10 support Balanced/Performance,
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

### Unclassified external power

Native ports managed's ordinary thermal control on unclassified external power
(`f6502f85`) for the setters it implements: Balanced and Performance, and
Fixed, Maximum and Automatic fans. Power is unclassified when Windows reports
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
so does a fan return to Automatic that carries a saved preset, which managed
applies through `SetPerformanceAsync`. Other fan setters on a classified class
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
`CreateObservedFallbackProfile`). Explicit Balanced and Performance overrides
apply; a request with GPU clock tuning is rejected before it is stored, Failed,
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
| External power (PluggedIn, limited USB-C or unclassified) with current-manual plus desired-manual fans, when a Balanced or Performance destination resolves | Stored, `Performance intent was stored and will be applied when fans return to Automatic.` |
| The same manual-fan state with no Balanced or Performance destination (for example a clear under a Silent or Custom profile) | Blocked, `Performance override was cleared. Hardware result: No supported performance destination...` |
| OpenBlade-owned fan row whose handback to Automatic is outstanding, without that manual deferral | Blocked, `Performance override intent was stored. Hardware result: OpenBlade still owns a fan change...` |
| GPU effect, ineligible policy, power change, admission, capacity, storage or transport cause after the intent was saved | Blocked, `Performance override intent was stored. Hardware result: <cause>` |
| Saved clear that was not applied | Blocked, `Performance override was cleared. Hardware result: <cause>` |
| Fan intent saved but not applied | Blocked, `Fan override intent was stored. Hardware result: ...` or `Fan override was cleared. Hardware result: ...` |
| Performance or fan clear on unclassified external power, with no owned fan change left | Stored, `Override cleared. Power-profile application waits for adapter classification.` |
| Fan clear on unclassified external power whose owned handback is not confirmed | Blocked, `Fan control could not be handed back to firmware. <cause>` |
| GPU clock tuning requested on unclassified external power | Failed, `The requested intent was rejected: This request needs classified power or includes native overclocking; ordinary external presence does not admit it.` |
| Unclassified 07/8C reading without fresh Windows AC presence, or with confirmed HyperBoost | Blocked, `Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked.` |

The Stored text is managed's `DeferredPerformanceIntentMessage`. Native
returns it only when the later fan request can apply the intent, so the
no-destination check runs before the manual-fan deferral. Managed defers there
too, because it can write Silent and Custom. A build
without `service-fan-mutation-v2` has no native fan request, so it keeps the
older instruction to submit a new performance request after selecting
Automatic fans.

### Apply on fan handback

Managed applies the saved performance intent when a fan request returns
manual fans to Automatic. On exact RZ09-0581 (direct manual transitions, no
performance report under manual fans),
`ControlCoordinator.ApplyDirectAutomaticFanTransitionOutcomeAsync` writes the
resolved preset's own 0D/02 pair, which carries the Automatic fan mode. It
does this when the saved fan intent before the request was manual
(`previousManual`), and replies `Fans returned to Automatic with <preset>
performance.`

Native does the same in the fan request. When a `setFanOverride` clear or
Automatic request leaves saved manual fan intent, the fan worker resolves the
destination as a performance request would (manual override, application
report, then the profile for the freshly read power class) and returns the fans
through that preset's pair instead of the observed selector. It is one direct
pair with the same bytes as the performance writer's pair; there is no fan
pair first and no reset. The existing fan-path admission applies to each
setter: fresh exact-device, caller, session, controller and lifecycle admission,
a fresh 07/8C read that agrees with Windows AC presence, and unchanged saved fan
settings and destination preset, also for a new class between setters. An
independent final 0D/82 pair must confirm
the preset with Automatic fans. The reply is `Fans returned to Automatic with
<preset> performance, confirmed by independent paired readback.`

The fan journal row records the destination selector. Its grammar is unchanged:
selectors 0 and 2 on sources 0 and 2 were already admitted, so the Rust and
managed fan-history readers need no change. Progress follows the fan chain
(Prepared, 10 to 13, 50). A power change before the first setter settles as
KnownNotSubmitted (6). A later stop, cancellation, rejected echo or readback
mismatch leaves the row unsettled and fences further writes; nothing is
replayed. A native-to-managed handback of such a row names the fan family with
Automatic only. Managed then confirms only whether the fans are manual
(`WriteConfirmationPolicy`) and, recovering from the native run, applies no
profile (`ServiceStartupCoordinator`), so the saved preset stays unapplied until
a later explicit request.

The fans keep the observed selector, and the saved intent stays unapplied, when
the saved fan intent was already Automatic, when no Balanced or Performance
destination is selected (managed would also write Silent or Custom, which this
writer does not admit), or when a GPU clock effect is saved; the reply then says
the GPU effect left it unapplied. The GPU check uses the same class expiry as
the destination, so a temporary override kept for another class does not count.
Managed's trigger is the request's saved fan intent, not an outstanding apply:
after a return refused before any setter (its Automatic intent is already
saved), a retry keeps the observed selector. When an Automatic request confirms
a selector other than the saved Balanced or Performance preset, the reply adds
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

Managed is the reference, and three shared vector files under
`tools/openblade-windows-host/tests/data` pin it:
`profile-set-vectors-v27.json` (decode, validation, reply and stored bytes),
`profile-aggregate-vectors.json` (loading and migrating schemas 1 to 27) and
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
  `text::same_ordinal_ignoring_case`, which the vectors pin to managed's
  `OrdinalIgnoreCase`: one character against one upper-case character, never
  an expansion (a sharp s is not `SS`) and never an ASCII letter for a
  character outside ASCII (a dotless i is not `I`). It rests on the Rust
  standard library's case table, so a letter whose case pair only one of the
  two tables knows can still differ.
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
`service-profiles-v1`, and neither package reader
(`native_package.rs`, `WindowsNativePackage.RequireCapabilities`) admits that
string, so a profile-serving image cannot be packaged, bound or installed as
the temporary-charge composition it extends. Tests on both sides pin the
refusal. Admitting it belongs to the packaging stage.

## Submission, ambiguity and finite transport lifetime

The portable typed pair resolves both 0D/02 reports before the first mutation.
Balanced and Performance use their destination directly when current admission
permits it. Exact0581 external-power current-manual plus effective-desired-manual
fan policy stores the request without setters; it does not restore per-preset
fans. The fan request that later returns the fans to Automatic applies it.
Automatic-active plus saved fixed fan intent remains direct. No blanket
Balanced/Automatic reset is an entry prerequisite.
The same native port worker and finite process transaction allocator are used;
all write, per-command power and final readback identifiers are reserved before
submission. Reopening a handle or replacing a worker does not reset that budget.
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
`profile_storage_real_tests.rs` drives the actual ProtectedFiles, with only the
security verifier injected, in a private directory under the temporary directory,
through a refused rename, leftover and held staging files, a recorded transaction
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
