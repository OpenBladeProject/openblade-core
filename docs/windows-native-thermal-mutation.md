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
the other performance slot as managed setters do. Temporary state expires when
a fresh known power source differs. Unresolved external power neither expires
intent nor establishes full-power effectiveness. Existing manual fan detail/custom/GPU fields
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

As in managed, every supported AC preset and the observed AC fan selector are
admitted on both classified external classes: an adapter at or above its
recommendation (PluggedIn) and a limited one (USB-C). The commands are the
same. Each setter needs a fresh adapter reading in the class first observed,
so a class change stops the pair without replay. A fan row can therefore journal
the Performance selector (2) with the limited source (2). Deployment readers
before `d6ce3339` reject such a row as invalid history, which fences
retirement, upgrade and mailbox custody fail-closed. The Rust `FanRecord` and
the managed `WindowsNativeFanHistory` reader both consume
`tools/openblade-windows-host/tests/data/fan-history-v3-limited-source.json`.
Battery presets are outside this writer. Managed's ordinary control on
unclassified external power, where the adapter reading is Unknown (exact
RZ09-0581, `f6502f85`), is not ported: native reports no power source for it and makes no thermal write.

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
| External power (PluggedIn or limited USB-C) with current-manual plus desired-manual fans, when a Balanced or Performance destination resolves | Stored, `Performance intent was stored and will be applied when fans return to Automatic.` |
| The same manual-fan state with no Balanced or Performance destination (for example a clear under a Silent or Custom profile) | Blocked, `Performance override was cleared. Hardware result: No supported performance destination...` |
| OpenBlade-owned fan row whose handback to Automatic is outstanding, without that manual deferral | Blocked, `Performance override intent was stored. Hardware result: OpenBlade still owns a fan change...` |
| GPU effect, ineligible policy, power change, admission, capacity, storage or transport cause after the intent was saved | Blocked, `Performance override intent was stored. Hardware result: <cause>` |
| Saved clear that was not applied | Blocked, `Performance override was cleared. Hardware result: <cause>` |
| Fan intent saved but not applied | Blocked, `Fan override intent was stored. Hardware result: ...` or `Fan override was cleared. Hardware result: ...` |

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
a fresh 07/8C read in the first observed external class, and unchanged saved fan
settings and destination preset. An independent final 0D/82 pair must confirm
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
performance request to apply it.` Owned Stop cleanup always hands back through
the owner's selector: shutdown reaches the firmware handoff without applying
saved settings. Native has no automatic reconciliation, so fans returned to
Automatic by firmware or another controller do not apply it.

Native never replies Failed. These managed Failed cases have no unambiguous
native counterpart, so native keeps Blocked:

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
- Intent rejected with nothing saved. Native replies Blocked, `Performance intent
  was not accepted...` or `Saved configuration replacement failed...`. Managed
  replies Failed, `The requested intent was rejected: ...`, except Blocked for a
  quarantined configuration. Native saves after the hardware observation and
  uses one refusal path for quarantine, an unsettled journal, validation and file
  errors.
- The same classes in fan replies, which are Blocked whenever the fan intent is
  not applied.

Mapping any of these needs native error classification first; a status change
alone would misreport the other class.

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
Full native-read/managed-codec parity, including that combination, remains future
work; the confirmed-only Custom correction grants no new write authority.

## Offline evidence boundaries

The focused native dispatcher tests use the actual installed dispatcher, runtime,
Storage, ReadSource, typed Reader and native worker lifecycle with explicitly
injected memory-only metadata, caller, custody, files and ports. Separate prelisted
own-user private-file tests use the actual ProtectedFiles and durable Store,
including first reservation, atomic journal replacement, exclusive sharing,
restart and cross-SID refusal. Constructors take those inputs before any default
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
