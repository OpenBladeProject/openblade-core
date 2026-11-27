# Native Service keyboard lighting (slices L1 and L2)

A Service built with `service-lighting-v1` serves `setLightingOverride` on
exact RZ09-0581 (`1532:02E0`). Slice L1 admits the keyboard Off with every
logo mode. Slice L2 adds the keyboard matrix on its second HID collection and
the Static effect, drawn by a Service-owned renderer with managed's 5 s
keepalive. The animated effects wait for slice C. The feature is
source-reviewed against managed and the reviewed capture evidence, has
software failure and recovery tests, and has not been run on a laptop. Both
package readers refuse it by name, so no packaged image carries it.

## Commands and admission

The job sends only commands the reviewed RZ09-0581 lighting evidence admits:

| Command | Use | Source |
|---|---|---|
| 03/83 | keyboard brightness query | `Rz09_0581_02e0_3_01_LightingSemanticAdmission.json` `brightness.query` |
| 03/03 | keyboard brightness set | `brightness.setPrefix` |
| 03/80 | logo state query | `logo.queryState` |
| 03/82 | logo effect query | `logo.queryEffect` |
| 03/00 | logo state set | `logo.setStatePrefix` |
| 03/02 | logo effect set | `logo.setEffectPrefix` |
| 00/86 | keyboard layout query, once per session | `Blade16KeyboardLightingLayout.json` `selector.query`; oracle `transitionRequests[0]` |
| 00/84 | device-mode query before the handshake | manifest `deviceMode.query`; oracle `transitionRequests[1]` |
| 03/0A:0500 | lighting transition handshake, exact echo | oracle `transitionRequests[2]`, `transitionResponses[2]` |
| 03/0B | matrix frame, a 375-byte feature report on the matrix collection | manifest `matrix` |

The setters, the matrix frame encoder and the handshake exist only behind the
protocol crate's `lighting-setters` feature, which only `service-lighting-v1`
enables. One fixed-length builder encodes each control report
(`openblade-protocol/src/lighting.rs`, `lighting/matrix.rs`); the encoder
allowlist (`device_mode_guard_tests.rs`) records both, and the lighting guard
(`lighting_guard_tests.rs`) pins their users and IDs.

The matrix codec is managed `Pid02E0MatrixProtocol`: a 374-byte envelope of
102 slots in 6 rows of 17, sent behind report ID 0, with the 16 canvas holes
black. Static paints every canvas key one color and projects it onto the
physical map the layout byte selects (managed `Pid02E0RegionalProjection`),
from the exact MCU source maps in managed's embedded
`Blade16KeyboardLightingLayout.json`. Native loads that file once, refuses it
unless its SHA-256 (`18771140...2AE0FB`) and every source identity managed's
static constructor requires hold, and leaves the matrix inadmissible
otherwise (`performance/lighting_layout.rs`). Matrix transaction IDs cycle
through 1 to 255.

Admission mirrors managed `ProductionLightingEvidenceSource.IsProductionLightingImplementationApproved`
(`ProductionLightingFactory.cs:74`): the embedded semantic admission manifest
must hash to managed's approved SHA-256 (`9F70CE88...DEED31`), and its device,
control envelope, device-mode, brightness, logo and matrix sections, with Off
and Static among the supported effects, must equal what the native codec
builds (`performance/lighting_admission.rs`). The matrix section is rebuilt
from the codec: its geometry, holes, layout pin, the regional projection
fingerprint and the known-answer feature report (transaction 90, color
`47E10C`, SHA-256 `7AFEE96E...76C98F`). The validated oracle must hash to its
pin (`5177FA9F...`) and carry exactly its reviewed members, capture pins and
protocol notation, built from the native codecs. Any difference refuses every
lighting write. Managed checks the oracle only in its tests, so this check is
stricter.

Each exchange and each frame runs under fresh exact-device,
competing-controller, power, session and ownership admission
(`Fresh::authorize`), through the Service's own HID lease. The Service stays
the only HID owner. The matrix collection is opened in the same job: exactly
one HID interface of the admitted device may report usage page 0x0C, usage 1
and a 375-byte feature report (managed's opener), its descriptor is read
again before each frame, and the handle is released with the job
(`RoleIdentity`, `MatrixLease`). A frame is one `IOCTL_HID_SET_FEATURE` of
the fixed report, so a short write cannot be expressed; a submitted write
whose completion fails is in doubt.

## Request order

The order is managed `ExecuteScopedMutationAsync` (ServiceControlRuntime.cs
14868-15060) for the Lighting subsystem:

1. Low-power gate set (lifecycle triggers): the intent is stored and nothing
   is read or written. The reply is Stored with the committed message (see
   the interim divergences). A temporary override needs Windows battery, the
   one class known without an adapter read; otherwise it is Blocked with its
   own text naming the low-power state (see Stricter than managed).
2. A possible earlier submission left unconfirmed: a read-only job runs, the
   reply is managed's pending refusal (`PendingWriteBlockedResult`, 19255),
   and a complete read clears the doubt. Nothing is replayed.
3. Keyboard delegated to Windows Dynamic Lighting in the stored preferences
   (`IsKeyboardLightingDelegated`): Blocked, managed's delegation text, before
   anything is stored. Managed checks it in the apply, after the store, and
   replies with the committed prefix (14965, 15054, 15213); see Stricter than
   managed. Without a profile store there are no stored preferences, and
   managed's defaults do not delegate.
4. Power, inside the job under its own admission (`Reader::lighting_power`):
   Windows battery is the battery class with no adapter read. On Windows AC
   one fresh 07/8C reading, the query every thermal recipe reads, classifies
   the adapter, and Windows must still report AC after it. A classified
   adapter admits lighting. An unclassified one needs managed
   `CanAdmitUnclassifiedExternalControl` for Lighting
   (UnclassifiedExternalPower.cs 60-77), each clause from native's own fresh
   evidence: fresh Windows AC presence; no unsettled journal step
   (`Storage::pending`: the store's quarantine or an unsettled last row of the
   one native journal, which holds the thermal, fan and battery rows; native
   keeps no other battery or HyperBoost doubt, so it stands for managed's
   pending indeterminate HyperBoost intents, and it is not managed's
   `WritesBlocked` status flag); and, with owned HyperBoost, managed's journal
   absent or Relinquished and no native ownership in the history. Native also
   requires a fresh firmware observation that HyperBoost is not engaged, and
   a failed one admits nothing; managed's Lighting arm has no such clause (see
   Stricter than managed). Windows unknown, an adapter battery
   reading Windows AC contradicts, or a refused clause is Blocked with
   managed's unclassified-adapter text (14935-14943), before anything is
   stored.
5. The class becomes current, with its Windows power generation, as a
   thermal step's store makes it, so a temporary override survives the next
   thermal step on the same power. The intent is stored
   (`SetLightingOverrideCoreAsync`, 14820) in `manual-overrides.json`
   `overrides.lighting`, in managed's format and member order, normalized as
   managed `SaveAsync` normalizes it. Only a persistent override is saved; a
   temporary one clears the saved one and is held in memory for its power
   class. A failed store is managed's rejected intent (`RecordRejectedIntent`,
   20495). Only a quarantined override document refuses the store: no thermal
   journal row or recovery rewrites the lighting member, so an unsettled
   thermal step does not. A clear is stored and writes nothing: on an
   unclassified adapter the reply is managed's Stored "Override cleared.
   Power-profile application waits for adapter classification." (15012); on a
   classified one see the interim divergences.
6. Apply. Off: read the baseline, write the brightness, read it back, then
   the logo effect (for a lit mode) and state, each read back, then managed's
   aggregate read. Static: see the renderer below. A confirmed mismatch
   restores the baseline. A setter or frame that may have been submitted
   without confirmation, or a final read that cannot confirm a written member
   (managed's indeterminate write with confirmation unavailable), stops the
   request and latches the doubt in step 2. A reply other than Applied is
   prefixed "{committed} Hardware result: ", as managed prefixes it.

Native's fresh admission confirms competing-controller absence before the job
(`capture_intent`) and at each of its checkpoints, the one before the 07/8C
read included, so a controller blocks before anything is stored. Managed's
refresh after power resolution
(`RefreshControllerPresenceAndRelinquishLightingAsync`) only observes the
controller and relinquishes lighting: it stores at 14965 and refuses the
apply afterwards (see Stricter than managed).

A zero brightness confirms Off only after a second zero sample 750 ms later
(managed `OwnedRendererOffObservation`), at the baseline and at the final
read, for every Off write: a keyboard lit again between the samples is
written. The read-only resolution samples a zero twice too, and a single zero
sample, such as a brightness setter's readback, confirms nothing, so status
publishes the Off record only from a double-sampled read
(`a_single_zero_brightness_sample_publishes_no_confirmed_off`).

## The Static renderer

A Static request runs managed `ProductionLightingPort.SetLightingAsync` for a
lit effect (`performance/lighting/render.rs`):

1. Open the matrix collection. One that does not open sends nothing and
   replies "Lighting write was not applied: The keyboard matrix collection is
   unavailable; no lighting setter was submitted."
2. Read the baseline brightness, the logo when one is requested, and the
   00/86 layout byte when this session has not read it.
3. A request for the owned renderer's color, unless the keyboard wakes from
   dark, keeps the renderer (managed `CanRetainRenderer`) and writes only the
   brightness and logo that differ.
4. Otherwise: the brightness first unless waking from dark; the 00/84 read,
   which must report Normal or Driver mode, and the 03/0A:0500 handshake; the
   first frame; the brightness when waking from dark, so the keyboard stays
   dark until the frame is shown; then the logo. Every setter is read back.
5. After a known failure (a readback of another value, a device mode outside
   Normal and Driver, or a frame refused before submission) the job restores
   the confirmed brightness, the previous renderer's handshake and frame when
   this job's handshake may have replaced it, and the logo, each once, as
   managed's rollback does. A restore that is itself in doubt stops there
   (managed `RollbackIndeterminate`) and latches the doubt.

A completed request replies Applied with managed's `AppliedWithoutReadback`
text: "Lighting command completed, but this laptop does not report the
resulting state." The lit state is never published as confirmed:
`confirmedState.lighting` stays null for a lit brightness.

The Service then owns the renderer. The origin tick, after the power check
and before the keyboard Driver-mode step, sends one keepalive frame when it
is due: every 5 s on the grid the first frame started
(`Pid02E0LightingTiming.IdleKeepaliveInterval`, managed
`AdvanceFrameDeadline`), so a late tick skips the instants it missed. Each
frame takes the owning request's fresh write admission. A frame refused
before submission keeps the renderer for the next instant (managed's
deferral); one that may have been submitted unconfirmed ends the renderer and
latches the doubt. A request holding the gate defers the frame to the next
tick. The renderer ends without a write when an Off may have changed the
keyboard, when a doubt is latched, at the low-power boundary (the gate set,
unreadable or exhausted), and at Stop, where the mutation worker's stop
cancels a frame still running. The session's layout byte is read again
afterwards.

Managed's lighting port enters Driver mode when it opens
(`EnsureDriverModeAsync`, ProductionLightingFactory.cs 226-242). Native sends
no 00/04 for lighting: the mode setter belongs to the keyboard Driver-mode
owner (`service-keyboard-driver-mode-v1`), which keeps Driver mode itself when
composed, and the lighting contract admits no mode setter. The handshake's
00/84 read admits Normal and Driver, as managed's does. A Service without the
keyboard owner therefore renders in Normal mode, the mode of the bounded
OpenBlade run managed's handshake comment records. That is source-backed
inference; the attended validation below covers it.

## Status

The status projects `lightingOverride` and `desiredState.lighting` (source 4
for a persistent override, 3 for a temporary one) from the store, in every
request's status, the thermal and fan requests' included
(`Runtime::project_saved_members`). A request's fresh reading joins the
confirmed state a thermal read established: `confirmedState.lighting` is the
Off record for a zero brightness and null otherwise (managed
`ReadStateCoreAsync`, ProductionLightingPort.cs 186), and
`confirmedState.logoLighting` is the managed wire value. A member whose setter
may have been submitted, or whose last read was outside the contract, is null
until a read confirms it. The native status carries no `unconfirmedWrites`
member, so the doubt is published only through those nulls and the pending
refusal. A keepalive publishes nothing.

Lighting records are written with `kind` first in every object
(`openblade_domain::kind_first`): managed binds `LightingParameters` with a
polymorphic `kind` discriminator and refuses it out of place. The status
records (`FeatureRecord`) and the thermal convergence record use it.

## Interim divergences

Each is named by a test where one applies:

- The animated effects are stored and then refused as managed refuses an
  effect its capability table lacks, until slice C
  (`animated_effects_are_refused_until_slice_c_divergence`).
- Clearing the override on a classified adapter stores the clear, applies no
  profile lighting and replies Stored, "Lighting override was cleared. The
  profile's lighting was not applied." Managed applies the profile's lighting
  there (ControlCoordinator.cs `ApplyLightingOutcomeAsync`)
  (`a_clear_is_stored_and_says_what_waits`).
- Power-source transitions (`ApplyFullTransitionAsync`, ServiceControlRuntime.cs
  14948-14956) and profile reconciliation run no lighting step, and a
  restart writes no lighting
  (`a_restart_keeps_the_saved_intent_and_writes_nothing`). So a request
  stored while the low-power gate is set is not applied after standby, and
  its reply is the committed message, Stored, instead of managed's "Lighting
  intent was stored and will be applied after standby."
  (`the_low_power_store_promises_no_resume_apply_interim_divergence`).
- At the low-power boundary the renderer ends without a write; managed turns
  the keyboard Off (`TurnOffForLowPowerAsync`) and restores lighting after
  the resume (`low_power_retires_the_renderer_without_a_write`).
- The periodic thermal observation does not read lighting, so the next
  thermal read publishes the lighting members as null until a lighting
  request reads them again. Managed reads lighting in every observation
  (`a_thermal_read_publishes_no_lighting_members_interim_divergence`).
- The status carries no `lightingRendererOwnership`.
- `setExternalLightingOverridePolicy` is not served and gets the generic
  unsupported reply
  (`every_absent_managed_operation_gets_the_typed_unsupported_reply`).

## Other differences from managed

- A Static request takes managed's setter-completion evidence from its own
  setter readbacks and does not repeat managed's final aggregate read, whose
  lit brightness confirms nothing.
- Managed's retain test compares effect, color and parameters. Static's
  parameters carry only its color, which well-formedness makes the settings
  color, so native compares the color and the session layout.
- A known failure before this job's handshake restores no frame: nothing
  changed the matrix, so the previous renderer keeps drawing. Managed redraws
  it.
- A device mode outside Normal and Driver is a known failure that restores
  the brightness and latches nothing; managed raises it as an indeterminate
  write after its rollback.
- A keepalive frame is up to one 500 ms origin tick late.

## Stricter than managed

Each of these is refused before anything is stored; managed stores it:

- A keyboard delegated to Windows Dynamic Lighting. Managed stores the intent
  and replies "Lighting override intent was stored. Hardware result: ..."
  with its delegation text (ServiceControlRuntime.cs 14965, 15054, 15213)
  (`a_keyboard_delegated_to_dynamic_lighting_blocks_before_any_store`).
- A competing controller. Managed's refresh only observes the controller and
  relinquishes lighting, so it stores and refuses the apply afterwards
  (`refused_admission_stores_and_sends_nothing`).
- An unclassified adapter while firmware reports HyperBoost engaged, or when
  that fresh observation fails. Managed's Lighting arm has no
  `HyperBoostActive` clause (UnclassifiedExternalPower.cs 60-77)
  (`an_unclassified_adapter_admits_lighting_only_without_hyperboost_or_a_pending_step`).
- A temporary override while the low-power gate is set, off Windows battery.
  Managed stores it with no power class (14903-14918); native's refusal names
  the low-power state
  (`a_low_power_temporary_override_needs_windows_battery_divergence`).

Also stricter:

- Every Off write, and the read-only resolution, confirms a zero brightness
  with the second sample; managed samples twice only while it owns a
  renderer.
- A setter, handshake or frame that may have been submitted without
  confirmation stops the request in doubt with nothing replayed; managed
  attempts its rollback (`an_unconfirmed_frame_stops_the_job_in_doubt`).
- A legacy Static request without parameters is refused before any write;
  managed refuses it inside its recipe, after the brightness.
- The validated oracle is checked at admission.
- The saved-override loader refuses a document managed would migrate (an
  older schema, or lighting that managed's visibility normalization changes)
  and preserves its bytes for managed.

## Guards

`lighting_guard_tests.rs` fails when non-test source names a lighting setter,
the matrix frame encoder or the handshake outside its codec, the recipe and
the semantic admission, builds a lighting job outside `lighting_mutation.rs`,
changes the class, query or setter IDs the codecs build, or enables
`lighting-setters` from another feature.

## Validation still needed

Attended validation on exact RZ09-0581, through the native FFI and HID
transport, which no laptop has run:

- Off from a lit keyboard with each logo mode, readback confirmation, a
  deliberate restore after a mismatch, and restoration of the saved baseline
  afterwards.
- Static from a lit and from a dark keyboard: the matrix collection selected,
  the layout byte read, the handshake, the first frame's colors on each
  layout in use, the keyboard kept dark until the frame when waking, the
  keepalive cadence over several minutes, and Off afterwards.
- Stop and a low-power boundary while the renderer owns the keyboard.

Record the keyboard device mode (Normal or Driver) of each run. Managed
writes these commands in Driver mode; a Service without
`service-keyboard-driver-mode-v1` is expected to write them in Normal mode,
which no physical run has observed for the setters. When the keyboard feature
is composed, run the checks in both modes.

## Follow-ups

- Slice C: the animated effects on the same renderer.
- Lighting in power-source transitions and profile reconciliation, the
  periodic lighting read, managed's low-power Off and resume restore, and the
  published renderer ownership.
- The native fixture-manifest corpus for lighting must land before lighting
  joins a packaged tuple or makes the Tray path reachable.
