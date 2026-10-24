# OpenBlade agent guide

This file is the first instruction source for AI coding agents working in this
repository. Keep changes narrow, evidence-based, and suitable for a native
Windows hardware utility.

<!-- trail:start -->
## Trail — repo context graph

This repository is indexed in `.trail/cache/`. The graph links concise system
notes to exact source spans and precomputed symbol relationships. For code
tasks, query Trail before broad text searches or broad source reads, and reuse
literal identifiers such as a symbol, error string, or file name.

Treat graph results as navigation and context, not as a substitute for the
current checkout. Source and tests remain authoritative. Verify current source
before consequential edits, safety or compatibility claims, and final review;
documentation, configuration, generated files, and new files may be unindexed.

Choose the command that matches the question:

- `trail map` gives a token-budgeted orientation to directory clusters, hubs,
  and hotspots. Use it when the repository or subsystem is unfamiliar.
- `trail ask "<question>" --source` returns ranked nodes with relevant source
  spans inlined. Use the results as focused starting points; add `--full` when
  a returned crux is truncated or the complete definition is needed.
- `trail grep "<literal>"` searches indexed files exhaustively and groups hits
  by enclosing symbol. Use it for every occurrence or caller of a pattern;
  use `rg` for files Trail does not index.
- `trail skeleton <file>` lists each definition's signature and span for a
  compact view of an API surface.
- `trail callers <symbol>` shows incoming references. Add `--direction out`
  for dependencies or `--depth N` to inspect a transitive blast radius.
- `.trail/cache/INDEX.md` is available for direct browsing. In multi-repository
  workspaces, narrow queries with `--in <scope>/` when the scope is known.

Open the exact referenced source range when Trail omits a required detail or
when verification is consequential. Read more surrounding source when needed
to understand the contract or validate an invariant; avoid rebuilding context
from whole files without a concrete reason.

`trail build` refreshes the deterministic graph without a model or API key;
`trail check` checks branch changes against recorded team learnings. Optional LLM
summaries require a separate `trail build --deep`.
<!-- trail:end -->

## Parallel work

Use parallel subagents when a task has multiple independent workstreams and the
expected speed or quality gain justifies the added token and coordination cost.
Prefer delegation for bounded, read-heavy work such as codebase exploration,
reviews, test or log analysis, triage, and research. Give each subagent a clear,
non-overlapping scope, wait for all requested results, then synthesize and
verify them in the main task.

Keep dependent work and overlapping code edits with one owner. Do not delegate
trivial tasks or split write-heavy work when merge conflicts and coordination
would outweigh the benefit. When parallel editing is useful, assign disjoint
files or components and inspect the combined diff before verification.

## Proportionate execution and authorization reuse

Prefer Synapse's exact-model or demonstrably shared vendor definitions as the
first evidence source for hardware semantics. Pin and review that source before
implementing it. Use user diagnostics when Synapse data is missing, does not work,
or conflicts with observed behavior; do not require users to repeat evidence
already established by vendor code. Distinguish source-backed behavior from
physical observations. This preference does not admit new firmware commands,
weaken exact-device checks, or waive fresh per-write safety admission.

Use the smallest workflow that resolves a concrete uncertainty and completes
required verification and reviews. Before adding a check, capture, study, or
validation step, identify what remains uncertain and how its result will affect
the next action. Reuse conclusive evidence while the relevant source, device,
and conditions remain unchanged; repeat a check only when a change, failure,
or unresolved concern justifies it. Exact-head and fresh per-write safety
checks remain required where specified below.

Reuse explicit user authorization within its stated scope throughout the task.
Do not ask again for the same authorized action merely because execution moves
to another step or agent. Ask when the next action exceeds that scope or an
existing rule requires fresh approval, including attended sleep/resume testing.
This policy grants no hardware authority and waives no required review,
approval, or safety requirement.

For authorized hardware validation, prefer one captured, typed, reversible test
that resolves the uncertainty: read and save the confirmed baseline, pass fresh
exact-device and applicable safety admission, apply one value, confirm it by
readback, restore the exact saved state, and verify restoration by readback.
Add further tests only for a concrete remaining uncertainty or required gate.
If submission or readback is ambiguous, stop further writes and reconcile
read-only. If restoration fails or cannot be confirmed, stop and report the
unresolved state; never replay an uncertain write, guess, or widen authority.

## Direct transitions

Apply the requested destination through the shortest supported typed command
sequence. Prefer direct transitions across performance, fans, HyperBoost,
power-source changes, lifecycle cleanup, battery, lighting, and display controls.
Do not reset to Balanced, Automatic, Off, or another default as a blanket entry
requirement.

Every prerequisite gate or intermediate hardware write must have a concrete
reason: a firmware command contract, an observed direct-transition failure, or
an actual cleanup or restoration dependency. Document that reason near the gate
and in its focused test. A captured starting state is validation provenance,
not proof that firmware requires that state. Missing profile permutations or a
large validation matrix do not establish a prerequisite.

When removing a gate, trace the complete path: protocol admission, service
transitions, desired-state normalization, profile validation, recovery, and UI.
Remove related forced defaults, redundant writes, and user-facing restrictions
together. Preserve saved intent, and distinguish requested settings from what
firmware can actually report; an unreadable field must not become a guessed
confirmation.

For transitions using commands already admitted for the exact device, resolve
the new uncertainty with a short typed, reversible round trip. Validate each
materially different command sequence or backend; do not require every profile,
fan subtype, or connector combination when source inspection shows the same
commands and relevant conditions. Record the device and conditions exercised,
and any behavior left untested. Reuse existing capture evidence; require new
captures only for an unknown command or a concrete unresolved result.

Tests should assert the requested outcome, necessary ordering, and failure
handling rather than preserve historical intermediate states. Expand validation
only when a changed path, observed failure, or specific unresolved risk warrants
it. During shutdown or confirmed low-power cleanup, reach the required firmware
handoff directly instead of restoring user settings only to undo them again;
keep those settings saved for the next active reconciliation.

Direct transitions retain fresh exact-device, capability, controller, power,
session, ownership, and applicable readback admission. If a failed or canceled
operation may have submitted a write, or confirmation is ambiguous, stop and
reconcile read-only; do not try an alternative sequence or replay the uncertain
write. Existing authorization and attended sleep/resume requirements still apply.

## Shared fixes and model scope

For every fix, check whether its cause and benefits apply to shared code or
other models before choosing its scope. Trace the relevant production paths
across supported and experimental backends. Apply common correctness,
synchronization, cancellation, resource-lifetime, diagnostics, and recovery
improvements at the shared layer when the same contract applies. A report or
reproduction on one model is not a reason to restrict a generally useful fix
to that model.

Use a model-specific gate only for a concrete device, firmware, command, or
backend difference; state that reason near the gate and in its focused test.
Check whether an existing gate leaves another path with the same defect, and
remove unnecessary model restrictions together with the fix. Preserve exact
device admission and model-specific command evidence: shared software hardening
does not authorize new firmware commands or capabilities. Verify materially
different paths proportionately, reuse unchanged shared-path evidence, and
report which models benefit, which already have equivalent handling, and any
remaining uncertainty.

## Product boundary

OpenBlade is a lightweight Windows utility for Razer Blade laptops. The
architecture admits capabilities only for an exact product name and HID
identity. Physically validated capabilities use sanitized, reviewed capture
evidence. Experimental capabilities use separately reviewed, pinned upstream
model and command definitions; they remain experimental until physical
validation is complete. Host admission verifies the Razer manufacturer and
exact product name in both Windows registry sources; SKU is diagnostic metadata
and must not reject a variant of an admitted product. Developer validation may
pin a SKU for reproducibility. Hardware-specific backends retain their own
device and driver checks.
Captured BIOS versions are provenance by default; a capability may impose an
exact firmware restriction only through an explicit reviewed admission rule,
and no shipped capability imposes one today.
Never copy a command from another model, expose raw firmware commands, or
weaken exact-device checks to make a feature appear supported.

Physically validated support is exact-device scoped: the Razer Blade 16
RZ09-0581 (`1532:02E0`) and RZ09-0528 (`1532:02C6`). Neither model's
device-specific assumptions may be copied to another.

The separately admitted experimental profiles are RZ09-0483 (`1532:029F`),
RZ09-0482 (`1532:029D`), RZ09-0530 (`1532:02C5`), RZ09-0328 (`1532:0255`),
and RZ09-0484 (`1532:02A0`). Their available controls are enabled automatically
when the exact profile is recognized, without a Settings opt-in or release-channel
gate. See [experimental support](docs/experimental-blade-support.md) for the
bounded capabilities, upstream provenance, write-pause recovery, and tester
reports. These profiles do not inherit another model's backends, drivers, or
physical-validation status.

## Source-backed experimental admission

When adding a supported model, review every entry in the blade-driver-catalog
applicability ledger against exact-model vendor evidence. Update its reviewed
profile list and each artifact's Applicable or NotEstablished decision, then
refresh the offline catalog fixtures in `OpenBlade.Tray.Tests`. Shared files do
not establish applicability of a complete driver stack. The coverage tests
compare the ledger with `RazerBladeModelCatalog.All` so a new model cannot skip
this review. Catalog applicability does not grant runtime hardware admission.

Physical validation is the promotion gate for validated support, not a
prerequisite for releasing a separately implemented experimental control for
community testing. The maintainer need not own every experimental laptop.
Review and pin the exact model's vendor or upstream command definitions, expose
its experimental provenance in capabilities and diagnostics, and document what
users still need to test. Do not claim another model's physical results.

When an exact model's reviewed HyperBoost implementation uses the same typed
thermal commands with or without a cooling pad, those commands may be offered
on both paths experimentally. Record padless use as source-backed extrapolation
until community validation is reviewed. Do not fabricate a pad connection or
issue additional accessory-status writes to emulate one. Preserve any concrete
firmware prerequisites established by the exact model's source.

Keep fresh exact-device, controller, power, session and ownership admission,
independent readback, durable restoration, and stop-on-ambiguity behavior on
every experimental path. Software tests must cover failure and recovery before
release. Physical-validation manifests belonging to the separately validated
backends remain required for those backends; they do not automatically gate
source-backed experimental backends. New or unknown commands still require
separate exact-model source review. This policy authorizes no development-time
hardware writes, installations, or unattended sleep/resume tests.

## Codebase map

- `src/OpenBlade.Contracts`: shared service IPC, profiles, capabilities, and
  typed device state. Keep it platform-light and backward-aware.
- `src/OpenBlade.Protocol`: 90-byte Razer report encoding, validated command
  fixtures, checksums, transaction matching, and transports.
- `src/OpenBlade.Service`: LocalSystem device ownership, automation, conflict
  handling, telemetry, configuration, and named-pipe server.
- `src/OpenBlade.Tray.Core`: UI-independent presentation logic, service client,
  per-user preferences, and reusable bounded pumps.
- `src/OpenBlade.Tray`: WinUI 3 window, tray icon, Mica shell, controls, and
  current-user integrations.
- `src/OpenBlade.Session`: contracts for current-user Session Agent IPC.
- `src/OpenBlade.SessionAgent`: always-running current-user display, input, and
  host-rendered lighting work that cannot run correctly as LocalSystem.
- `src/OpenBlade.Display`: Windows display enumeration and mutation.
- `src/OpenBlade.Capture`: developer-only, typed protocol inspection and
  reversible validation commands. It is never part of the installed product.
- `src/OpenBlade.FirmwareSmoke`: developer-only installed-product validation
  before and after laptop firmware updates. It uses typed service IPC, requires
  a read-only green gate before reversible writes, and always restores state.
- `src/OpenBlade.Setup`: transactional install, repair, shutdown, and uninstall.
- `tests/*`: project-aligned tests plus `Build.Tests` release-payload checks.
- `evidence`: core-owned immutable sanitized legacy evidence and its provenance
  map. Use Synapse Observer for new observations.
- `build`: publish, package, and deployment scripts.

## Settings cell presentation

Use the Custom performance CPU/GPU subcells in `MainWindow.xaml` as the
reference for new settings subcells. Reuse the shared presentation rather than
inventing a page-specific appearance:

- Ordinary subcells use `SettingsNeutralSubcardStyle` or its last-row variant:
  transparent at rest, with the shared pointer-over background only on hover.
  Forms and multi-control editors use the hover-free `SettingsEditorSubcardStyle`
  variants. Permanent shading is not a nesting indicator; use an emphasized
  style only for an explicitly required visual role.
- Single-line rows reuse `DashboardSingleLineRowPadding` (16,4),
  `DashboardSingleLineRowContentMinHeight` (40), `CardTitleStyle`, centered label
  alignment, and 20-DIP label/control spacing. Keep the shared wrapped-title
  padding adjustment; do not fix row heights. Multi-line descriptions and
  editors retain the standard shared padding.
- Let shared styles own backgrounds, separators, corners, margins, and hover
  behavior. Do not override them locally or copy their values into new styles.
  Nested card expanders have zero content padding because their child rows
  already own padding. Collapse empty optional children so stack spacing does
  not leave a phantom row.
- Extend semantic shared-style tests, not just style-use counts. For a new row,
  compare its presentation with the reference and inspect a disconnected render
  in the relevant empty, populated, wrapped-label, and hover states. Reuse
  unchanged evidence; inspect only states affected by the change. A deliberate
  exception needs a concrete design reason in the PR and its focused test.

## Typography

Use the application typography roles in `Themes/Typography.xaml`. They inherit
the native WinUI type ramp so the system font, language fallback, text scaling,
and high-contrast behavior remain authoritative:

- `AppPageTitleTextStyle` for page titles and `AppSectionTitleTextStyle` for
  major sections.
- `AppBodyTextStyle` for ordinary text and table cells, and
  `AppBodyStrongTextStyle` for card titles and table column headers.
- `AppCaptionTextStyle` for units, timestamps, chart axes, and supporting
  metadata. It uses the theme's secondary text brush.
- `AppDataTextStyle` for numeric readings and table values. It uses regular body
  text with tabular numerals. Keep all table body cells regular; reserve strong
  text for headers.
- `AppCommandButtonStyle` for full-size string page commands. Its shared
  `AppCommandTextTemplate` wraps and centers localized body text while the
  button keeps native focus behavior, a 40-DIP minimum height, and shared
  padding. `AppCustomCommandButtonStyle` shares native sizing and padding for
  icon or composed content without applying the string-only text template.
  Other specialized controls retain their component style.
- `AppToolbarButtonStyle` and `AppToolbarToggleButtonStyle` for flat desktop
  tools with 32-DIP minimum targets and native interaction states. Use
  `AppToolbarIconStyle` for their shared 16-DIP decorative glyphs and keep
  accessible names on the controls. Body text retains the native type ramp.
- `AppInlineCommandStyle` for native text actions beside captions, such as
  Full range. Its 24-DIP minimum height can grow with text scaling.

Keep local layout styles as aliases based on these application roles. Do not
copy the native role's current `FontSize`, set a fixed line height, or introduce
a new font family. Let translated and scaled text wrap instead of fixing row
heights. Runtime-created text uses `AppTypography.Apply`; runtime-created
buttons resolve the command style through `AppTypography.RequireStyle`.

The existing tray quick-controls popup may retain its compact 12-DIP and 11-DIP
text until that surface is migrated and rendered independently. A new raw font
size needs a concrete constrained-surface reason and focused visual evidence.
See [UI typography](docs/ui-typography.md) for the semantic scale and table use.

## Architecture rules

1. The service is the sole owner of the laptop control HID handle. It exposes
   typed operations only and serializes all hardware access.
   On models that admit keyboard Driver mode, it owns that mode continuously
   while the laptop is fully active: it performs verified read-before-write maintenance and is the only
   component allowed to change that mode. At a confirmed low-power boundary it
   may restore Normal mode only after the existing identity and
   competing-controller safety checks pass, and it must not reacquire Driver
   mode until Windows reports that the system is active again. The Session
   Agent consumes confirmed status only and must pass function-row keys through
   whenever Driver-mode status is unknown. During coordinated shutdown, the
   service uses the same guarded Normal-mode release before bounded fan cleanup
   so a fan failure cannot skip keyboard cleanup. It hands OpenBlade-owned
   manual fan control back to firmware Automatic only after confirmed-state,
   exact-device, and competing-controller safety checks pass, without changing
   the saved fan intent. A first observation or matching desired state does not
   establish fan ownership; only a successful service fan write does.
   Capture-backed lighting admission is owned by the approved embedded oracle and
   the reviewed semantic admission manifest. The manifest compares the actual
   device identity, command contracts, report geometry, layout mask, supported
   values, and a known-answer encoded report; unrelated source edits do not
   require re-approval. Experimental lighting may render host frames through its
   own reviewed exact-model backend. Pin the model's transport, report geometry,
   address bounds, activation, ownership, and restoration behavior; preserve fresh
   admission and stop after ambiguous submission. Grid effects such as Fire and
   Wheel do not require a physical key-name map when the addressed matrix bounds
   are established. Key-triggered or per-key controls additionally require their
   exact input-to-light mapping. Keep RGB intent separate from firmware readback,
   and never inherit another model's frame mask, commands, or validation status.
   A machine-local
   exact-build validation artifact is developer evidence only and must never
   independently enable production writes.
2. The Session Agent owns current-user APIs and input observation. The visible
   tray may exit without disabling profile automation, display automation,
   interactive lighting, or configured special keys.
3. UI-only work must not rebuild, replace, or advance the installed service or
   Session Agent. Use the tray-only deployment path only when shared Contracts
   and both IPC versions are unchanged, and verify protected hashes. Any shared
   contract or IPC change requires a coordinated full install.
4. A temporary read, pipe, or enumeration failure is not proof that the laptop
   disappeared. Preserve last confirmed presentation through bounded retries,
   but perform fresh identity and safety checks before every write.
5. Never stop competing Razer software. Strict thermal control remains
   advisory for externally changed performance and fan state. Maintain desired
   state may reconcile confirmed thermal drift only after fresh competing-
   controller absence, exact-device, power, session, durable-budget, and
   readback admission; ambiguity or possible submission pauses instead of
   fighting or replaying a command. Only the user-enabled lighting ownership
   policy may restore lighting automatically.
6. Keep queues bounded, cancellations observable, writes atomic, and native
   handles deterministically released. Do not add a kernel driver or depend on
   Synapse components. As an optional integration, the native-brightness path
   may use each admitted model's own exact, signed Razer driver stack (`02E0`
   for RZ09-0581, `02C6` for RZ09-0528) when that stack is already installed
   or separately provisioned. Keep WMI fallback available, never mix one
   model's stack with another's, and never generalize a stack to an unadmitted
   model or API contract. Compatible installed driver updates follow the API
   compatibility policy below. Developer export tooling does not authorize installer
   bundling or publication without the corresponding redistribution
   authorization, notice, hashes, and signature validation.
   SnapTap may use the already installed, trusted model-specific keyboard
   driver profiles reviewed in [SnapTap](docs/snap-tap.md) through the shared
   typed A/D-only backend. Newly reviewed models remain source-backed experimental
   until their own physical SnapTap validation is complete. Keep exact host, driver,
   keyboard-collection and container admission, supervised subscription lifetime,
   owned-hook restoration and uncertain-output recovery. It must not load the
   Synapse mapping engine, acquire the service's HID handle, install or bundle
   drivers, or fall back to a system-wide keyboard hook. A separate, explicitly
   user-initiated elevated helper may install the pinned RZ09-0581 catalog stack
   after exact host, topology, package hash and Windows catalog verification.
   Keep its durable journal, independent readback and stop-on-ambiguity behavior;
   never install from the input worker or bundle vendor binaries. Other models need
   separately reviewed driver evidence; see [SnapTap](docs/snap-tap.md).
   The legacy THX COM backend, originally reviewed in 2.6.5.0, may offer manual spatial
   audio control on exact RZ09-0482 (`029D`), RZ09-0483 (`029F`) and RZ09-0484
   (`02A0`) when their trusted, model-bound service/APO stack is already installed. Keep its
   exact host/codec/output ancestry and form-factor checks, guarded COM
   registrations, isolated synchronous helper lifetime and explicit readback.
   EQ, presets, automation and fresh legacy provisioning are not admitted;
   SCU embeds exact-model acoustic tuning, and the catalog-authenticated THX
   extension supplies exact-codec child-component declarations. The complete
   endpoint FX binding and installation contract remain unresolved. Do not invent
   SWC nodes, change signed INF directives or write guessed endpoint APO registrations.
   Discovery never activates COM. The native integration remains experimental
   until exact-model physical validation; see [legacy THX](docs/thx-legacy-interface.md).
   The optional experimental THX audio integration may use the already
   installed, trusted `rznb12` API stack on exact RZ09-0581 (`02E0`) and the trusted
   `rznb11` stack on exact RZ09-0528 (`02C6`) and RZ09-0530 (`02C5`). It is off
   by default, uses current-user typed Audio IPC and an isolated native helper,
   and never bundles THX drivers or services. An explicit elevated setup action
   may provision the pinned rznb11 stack on exact RZ09-0528/RZ09-0530 or the
   separately pinned cached rznb12 4.2.37.1621 stack on exact RZ09-0581 with
   matching codec, AC power, no competing controller and no existing THX stack.
   Before THX, that fixed setup may provision Realtek 6.0.9906.1 on exact
   RZ09-0528/RZ09-0530 or Intel SST 20.43.12634.0 on exact RZ09-0581 from the
   pinned official Microsoft CABs. Require exact root identity, all root-INF
   matching targets, actual bound package/catalog evidence and fresh ancestry.
   Keep existing recognized base drivers; preserve/refuse unknown builds rather
   than force a downgrade. Verify Windows inbox SDCA audio roles through Windows
   servicing catalogs without bundling them or inventing a Windows version floor.
   Import only the reviewed root and its declared CopyINF closure; never use
   wildcard installation, forced replacement, guessed child creation or an
   automatic alternate sequence. Require independent base binding readback and
   retain numeric journal meanings with a versioned ordered recipe. This grants
   no development-time installation or hardware authority.
   Synapse must not be a prerequisite. The owner-confirmed Razer redistribution
   permission covers the original signed THX installer as a catalog release asset;
   retain its notices and exact source, size, hash and signer pins. Never bundle
   vendor bytes in OpenBlade or Git. Verify the carrier, statically extract the
   pinned complete inner Burn, verify its independent signature, and execute only
   that inner installer from protected staging. Never run the wrapper that extracts
   into a user-writable directory. Keep durable per-process intents bound to the
   whole recipe and independently verify installed catalogs/config/service. Never
   retry an uncertain submission or replace an existing admitted stack. Keep
   each catalog version as an exact installation recipe. Runtime admission uses
   the installed API contract and coherent bound packages, without a build allowlist.
   Require a successful typed read tied to the freshly verified runtime identity
   before mutations; an update invalidates cached registrations and reads. Each
   modern V4 runtime identity requires a complete first read of both endpoints,
   retaining the recovered 1621 pairing check without a build selector. The recovered
   1621 runtime and pristine installation remain experimental until physically tested. An explicit user
   action may download only the pinned native client from its reviewed Razer
   HTTPS URL into OpenBlade's per-user component directory, after exact size,
   SHA-256, Windows Authenticode trust and signer verification. Publish it
   atomically; acquisition must not load native code or change audio settings.
   Keep the verified Synapse-installed client usable, and retain the full
   model-specific API admission and locked verification of the OpenBlade-owned
   native client before loading. Its artifact hash does not whitelist driver builds.
   Do not bundle or redistribute the DLL. Component discovery and
   shutdown must not change audio settings. Automatic changes, including
   startup reconciliation, require both the per-user integration preference
   and the active profile's explicit audio automation opt-in, a known
   foreground executable, and the exact recognized active Realtek output.
   Preserve explicit readback and stop mutations after ambiguous submission
   until a successful explicit read. RZ09-0528 and RZ09-0530 remain
   source-backed experimental support until their native behavior is physically
   validated. This exception admits no other laptop or API contract family. Keep
   standalone native/audio validation separate from simulated tests; see
   [THX audio](docs/thx-audio.md).
7. Prefer one shared implementation or presentation primitive over
   page-specific fixes. When a bug can affect every picker, card, or firmware
   field, fix the shared layer and add a regression test there.
8. Do not add abstraction layers, background loops, or broad test matrices
   without a concrete current requirement. Favor the smallest design that
   fully implements the requested behavior and remains understandable.
9. Never silently drop a feature discovered on a different model. Represent it
   in the shared capability contract, diagnostics, model documentation, and
   gated UI even when the only honest access state is `ObservedOnly`. A missing
   backend or readback blocks mutation; it does not justify hiding the model
   difference or substituting another model's assumptions.

### API-based driver compatibility

Runtime access through an admitted Windows or vendor driver API must accept
compatible installed driver updates without requiring an OpenBlade release.
Do not use exact file hashes, package builds, minor versions or certificate
thumbprints as runtime allowlists. Preserve the exact model, device, topology,
service, endpoint and typed API/ABI contract admission for each backend.

Verify the actual installed package and service binding, current Windows
signature/catalog trust and required publisher, and catalog membership of the
files used. Bind publisher identity to the signer from successful trust-provider
state. Check the API/interface and validate bounded typed responses where that
contract provides a read-only probe. Do not invent probing writes. Reject an
untrusted, misbound or incompatible API with its concrete failure; a changed
binary alone is not evidence of incompatibility or a missing dependency.

Observed content fingerprints may identify a runtime instance, invalidate caches
or detect replacement. They must not become a release-maintained whitelist.
Revalidate the relevant binding and identity before mutations, keep verified
files stable where they are loaded, and preserve ownership, explicit readback,
uncertain-submission recovery and stop-on-ambiguity behavior. A compatible update
may establish a fresh trusted runtime; it must not silently inherit stale reads
or registrations. These checks do not claim physical validation of future builds.

Keep exact source, size, hash and signature verification for downloaded/exported
artifacts, catalog release assets, installer inputs, transactional recovery and
OpenBlade-owned executable components. Catalog hashes identify those artifacts;
they do not define which independently installed driver builds may expose a
compatible API. Actual API/ABI or capability changes require contract review,
rather than adding another binary hash to an allowlist.

This policy covers THX, SnapTap and native-brightness driver integrations. It does
not admit another model, a new firmware command or an unknown API, and it does
not weaken raw HID/firmware semantic admission or authorize development-time
hardware writes, installations, service changes or attended lifecycle tests.

### Tray async context

`OpenBlade.Tray.Core` deliberately uses two async-context modes. Command paths
capture the WinUI context: the bare `await Task.Yield()` in
`AsyncRelayCommand.RunAsync` is the mechanism, and `SetPerformanceAsync`,
`SetCustomPerformanceAsync`, `ApplyFanControlAsync`, `SetLightingAsync`,
`SetChargeLimitAsync`, `ToggleBatteryProtectionAsync`, and
`ToggleFullChargeAsync` mutate bound state in a `finally` after an await.
`UpdateCheckViewModel.RunAutomaticCheckAsync` and `CheckCoreAsync` also capture
the UI context because they mutate bound update status after awaiting the
release client. `FirmwareUpdateAssistantViewModel.RunAutomaticCheckAsync` and
`CheckAsync` capture it for the same reason after awaiting the Razer firmware
client.
`TrayCompleteShutdownCoordinator.ShutdownAsync` also captures it because its
Session Agent signaling and tray-exit callbacks are UI-affine.
`App.RunElevatedServiceControlHelperAsync` captures it after off-dispatcher
service work because `Exit()` must run on the WinUI thread.
Background pumps, loops, queues, and `ServiceClient` use
`ConfigureAwait(false)`.

xunit runs without a `SynchronizationContext`, so tests cannot distinguish
these modes or detect a wrong annotation in a UI-affine path. Do not enable
CA2007 or bulk-add `ConfigureAwait(false)` in Tray.Core.

## Hardware and capture safety

- Treat writes outside the exact model's reviewed command contract as unsafe
  and unsupported. The upstream-derived experimental backend admits only its
  implemented typed operations; its presence grants no additional developer or
  physical-validation write authority. Unknown commands still need reviewed
  evidence and separate authorization.
- `07/8F` and `07/0F` are a shared Power Mode Control bitmask on every model
  that uses them, not a full-charge-only value. Keep the shared decoder and
  pre-write guard, and before admitting a model, especially a mini-LED one,
  follow
  [Power Mode Control and local dimming](docs/developer-guide.md#power-mode-control-and-local-dimming).
- Keep validation proportionate. For transitions using commands already admitted
  for the exact device, prefer a direct test through OpenBlade's typed,
  reversible path. Vendor UI availability or a new capture matrix is not a
  prerequisite for that test. Add evidence steps only for a concrete risk, such
  as an uncaptured command, an unrecoverable state, or an unexplained readback.
- Read/query first. For a new write, save prior confirmed state, show the exact
  action, apply one value, read it back, and restore it.
- Explicit approval for unattended hardware validation extends only to
  captured, typed, reversible operations. Before each such operation, read and
  save the prior confirmed state, pass fresh exact-device and applicable safety
  admission, confirm the result by readback, and restore the saved state. If
  submission or readback becomes ambiguous, stop further writes and reconcile
  read-only. If restoration fails, stop and report it; never widen write
  authority or guess.
- Never initiate or arm sleep/resume validation unattended, including while
  the user is unavailable. Keep it as the last physical-validation step and
  require fresh approval immediately beforehand from a present user who can
  resume the system and report the outcome.
- Scope physical validation to the behavior and safety conditions exercised.
  When source inspection confirms that different power connectors use the same
  transition handler and relevant state checks, one attended test may validate
  that shared path. Record the connector used and any source-specific behavior
  left untested; do not repeat a disruptive lifecycle test solely to match the
  connector in the original report.
- Use [Synapse Observer](https://github.com/OpenBladeProject/synapse-observer)
  for new protocol observations. Save the baseline, record one deliberate
  action, restore the saved value, and verify restoration.
- The private legacy USBPcap tooling is obsolete. If a maintainer must
  reproduce an archived workflow, never run USBPcap capture and OpenBlade
  control against the same interface at the same time. `USBPcapCMD` must run
  in its own process group and receive a targeted `CTRL_BREAK_EVENT`; never
  call `GenerateConsoleCtrlEvent` with process group `0`. Force termination is
  an explicit fallback that may lose buffered capture data.
- Never commit raw PCAP files, device serials, user paths, or unsanitized
  payloads. Capture provenance and restoration results belong in the sanitized
  annotation.
- Preserve untracked `.capture-control-*` and `.query-control-*` directories
  unless their owner explicitly asks to remove them.

### Legacy evidence history boundary

Current source stores the sanitized evidence required by production admission,
tests, and documentation under `evidence/legacy-captures`. Its
`evidence/provenance.json` map records the original private repository, pinned
revision, original path, and SHA-256 for each imported file. A normal clone,
build, and test run needs no private repository access.

Older super-repo commits used a `captures` submodule. Its history was rewritten;
super-repo commit `568fa88` is the boundary. Commits before it reference
submodule commits absent from the remote, so historical recursive checkouts,
bisects across the boundary, and CI re-runs can fail by design. Investigate
those commits without recursing submodules.

## Change workflow

All work that may modify repository files must use a dedicated Git worktree for
the task. Do not implement changes, run change-producing builds or tests, stage,
or commit from the primary landing checkout, even when it is clean. Read-only
inspection may use the landing checkout. Create the worktree from `main` by
default unless the user explicitly requests another base, and preserve all
unrelated worktrees and checkout changes.

1. Inspect `git status`, the affected contracts, and existing tests before
   editing. User changes and unrelated dirty files are not cleanup targets.
2. Whenever creating an issue, apply the relevant repository labels.
3. Before implementing a fix, link it to existing tracking when that issue
   already covers the problem. Keep related diagnostics and implementation
   follow-ups in the parent issue and draft pull request; create a new issue
   only for a distinct unresolved problem. Deliver fixes through a pull request.
   When consolidating duplicates, preserve useful evidence and acceptance
   criteria in the parent, leave a link explaining each closure, and use the
   duplicate/not-planned closure reason. Keep unresolved hardware failures
   visible; passing source checks or preparing a package does not prove
   physical recovery.
4. Update the smallest authoritative layer. Do not duplicate state between the
   service, Session Agent, and tray.
5. Add focused tests for parsing, normalization, failure handling, and shared UI
   contracts. Run the affected project tests before the full solution.
6. For WinUI changes, build `OpenBlade.Tray` and inspect the rendered app when
   interactive access is available. XAML contract tests do not prove visual
   quality.
7. For hardware changes, complete the relevant physical validation before
   marking support production-ready, following Hardware and capture safety.
8. Update `CHANGELOG.md` under `Unreleased` for user-visible changes. Every
   new or materially changed changelog bullet must include a Markdown link to
   its implementing pull request, using
   `([#123](https://github.com/OSSBlade/openblade-core/pull/123))`. Reference
   pull requests, not tracking issues; include each implementing PR when a
   bullet combines changes. If the PR number is not yet available, open the
   draft PR first, then add the verified reference before marking it ready or
   merging. Preserve existing references and published release snapshots;
   reference-only corrections to `Unreleased` must link the original PR.
   The Nightly workflow treats each published tag as a release boundary, attaches
   an exact-source changelog snapshot, and opens or updates a draft pull request
   that moves included entries into dated `## [X.Y.Z-nightly.N] - YYYY-MM-DD`
   sections while retaining post-build entries under `Unreleased`. Review that
   generated diff; do not hand-edit the workflow-owned branch or bypass its
   exact-tag and force-with-lease guards. Keep entries as `- ` bullets under
   `### ` categories; continuation paragraphs, nested lists, and fenced blocks
   remain part of the preceding bullet. Editing a bullet already included in a
   published nightly creates a new entry at the next nightly boundary while
   the earlier section retains its exact published wording. Publishing a
   release by hand skips that job, so record the published section in the same
   change: regenerate with `Write-OpenBladeNightlyChangelogSnapshot` from
   `build/NightlyChangelog.ps1` and confirm with
   `build/Validate-ChangelogReleases.ps1`, which fails when a published release
   has no dated section or no comparison link. Nightly preparation now leaves
   a draft: review and merge the generated changelog PR before dispatching
   `finalize-nightly.yml` on `main` with its exact tag. Both hosted and local
   publication must use `build/Complete-NightlyRelease.ps1`, which validates
   live release history and the source snapshot against current `main` before
   making the draft public. A draft PR alone does not complete the changelog.
9. Commit and push coherent slices. Do not bundle capture evidence, product
   code, history maintenance, and unrelated formatting in one commit.
10. Preserve the line endings declared by `.editorconfig` and `.gitattributes`.
   Patch application can insert LF-only lines into an existing CRLF file, and
   `git diff --check` does not detect that mixed state. Before committing,
   inspect every touched text file for mixed endings and normalize the complete
   file to its declared convention. Treat Git's `LF will be replaced by CRLF`
   warning as unfinished work, not as a harmless staging detail.
11. Prefer the authenticated GitHub CLI (`gh`) over application connectors for
    GitHub reads and writes. Use a connector only when `gh` cannot perform the
    needed operation. If `gh` is available but genuinely unauthenticated, ask
    the user before falling back to a connector.
12. On managed hosts, do not treat a sandboxed `gh auth status` failure as
    proof that credentials expired when the same output includes config or
    keyring access-denied warnings. Retry the exact read with approved host
    access; ask for reauthentication only if that host-level check also fails.
13. After a pull request is merged or closed, complete its branch cleanup:
    remove any dedicated worktree, then delete the local and remote pull-request
    branch. First verify the pull request's final state and exact branch, preserve
    dirty or untracked worktree artifacts, and ensure no other live worktree uses
    the branch. For a merged pull request, also prove that the branch tip is
    reachable from `origin/main`. If any safety check fails, report the blocker
    instead of silently leaving cleanup incomplete or deleting uncertain work.
    After any successful branch merge, also update the primary non-linked-
    worktree checkout to the latest `main` with `git pull --ff-only origin main`;
    this synchronization is the required exception to the landing-checkout
    read-only rule above. Confirm first that the pull will preserve local
    changes. If local modifications or divergence prevent a safe fast-forward,
    report the blocker rather than stashing, resetting, or discarding work.

## README, documentation, and UI language

Use the concise [README](README.md), approved in
[PR #558](https://github.com/OSSBlade/openblade-core/pull/558), as the writing
reference for user-facing documentation and app UI text. These rules cover
labels, helper text, tooltips, status messages, notifications, and confirmation
dialogs in the tray, graphical installer, and native setup, including their
translations.

- Write for laptop owners in plain, direct English. Prefer familiar words,
  concrete actions, and short sentences over implementation jargon.
- Keep the README a brief introduction: purpose, supported laptops and Windows
  requirements, main features, installation, and links to detailed guides.
  The approved version is about 440 words; use that as a guide to brevity, not
  a rigid word limit. Put substantial new detail in a linked guide.
- Avoid lawyer-like disclaimers, defensive caveats, repeated warnings,
  promotional claims, and absolute promises about support or reliability.
- State a limitation once, near the relevant feature or step, when it helps
  the reader decide or act. Keep admission rules, compatibility checks,
  failure handling, and validation history in the relevant technical guide.
- Preserve real safety requirements and model-specific limits. Simplify the
  wording without weakening the requirement or overstating support. Distinguish
  tested firmware provenance from actual production admission restrictions.
- Prefer compact feature bullets to many small sections. Avoid repetitive
  bold-label lists, filler, and redundant explanations of what the app does
  not do.
- Write instructions positively and concretely, for example: "Start it
  normally; setup asks for administrator permission when needed."
- Review documentation and UI text edits for factual accuracy, length,
  repetition, and tone. Verify changed behavior claims against the implementation. Detailed
  engineering guides should retain the precision their readers need.

- Delete redundant disclaimers outright; do not replace a removed caveat with
  another warning or reassurance. Include a limitation when it changes what the
  reader can do, needs to check, or should expect.
- State actions positively, for example, "Start HyperBoost manually." Avoid
  stacking statements about what a feature never does. Routine entry and
  restoration behavior belongs in usage or technical detail when needed, not
  as a caveat attached to every feature description.
- Preserve meaning while shortening: safety gates, model and firmware limits,
  process exclusions, and benchmark sampling conditions must remain accurate.
  Keep English, Russian, and Chinese versions aligned.
- In UI text, state the current condition, consequence, and next action when
  needed. Keep data-loss warnings, uncertain-write and recovery instructions,
  and required safety or compatibility limits explicit. Remove internal
  implementation detail that does not help the user decide or act.
- Follow [the localization workflow](docs/localization.md) when changing UI
  text: update source fallbacks and matching translations, preserve placeholders
  and access keys, and regenerate the authoritative catalogs and pseudo locale.
- Review translations as a complete feature using its glossary, behavior and
  rendered screens. Give changed wording a separate language review; mechanical
  resource checks do not establish natural phrasing. Record uncertain terms in
  the PR and keep button labels consistent with instructions that name them.

## Versioning and commits

Follow [VERSIONING.md](VERSIONING.md). The short form is:

- Name branches for the work, not the agent or vendor. Use purpose prefixes
  such as `feat/`, `fix/`, `docs/`, `chore/`, or `refactor/`; do not use
  prefixes such as `codex/` or `claude/`.
- `Directory.Build.props` is the product-version source of truth.
- Every pull request must complete the `Version impact`, exact `Version change`,
  and `Reason` fields defined in `VERSIONING.md`. Apply any required bump to
  `OpenBladeVersion` in that same pull request, then refresh from `main` and
  recalculate before marking it ready or merging.
- Profile/config schema and IPC protocol versions are independent integers with
  explicit compatibility rules and migrations.
- Use Conventional Commits:
  `type(optional-scope): imperative summary`.
- Typical subjects are `feat(tray): add special-key bindings`,
  `fix(service): retain device state during reconnect`, and
  `chore(capture): record restored fan validation`.
- Do not capitalize the first word after the colon or end the subject with a
  period. Keep it at or below 72 characters when practical.

## Verification baseline

For a normal product change:

```powershell
dotnet build OpenBlade.slnx -p:NuGetAudit=false --no-restore
dotnet test OpenBlade.slnx -p:NuGetAudit=false --no-build --no-restore
```

On managed hosts, run the full solution test command above with approved host
access from the outset. The sandbox consistently denies the loopback client in
`NamedPipeControlServerTests.ServeOnceAsync_ReadsRequestBeforeResolvingWindowsClientContext`
with `UnauthorizedAccessException`; this is a sandbox permission failure, not a
test regression. Focused test projects that do not exercise Windows named-pipe
client identity may still run in the sandbox.

Use `build/Publish.ps1` and the payload validation tests for release or setup
changes. An explicitly approved unattended installation may use only the
enrolled fixed-operation local-development deployment broker, and only when
the approved operation can complete without displaying or requiring an
interactive UAC prompt. While the user is unavailable, never initiate or wait
for UAC, invoke generic elevation, use the broker for generic command
execution, bypass its fixed-operation authorization, or fall back to another
installation path.

Do not run production publishing from a OneDrive-linked worktree. The deep WiX
upgrade-probe extraction path repeatedly fails during validated cleanup there,
even after the binaries and installers build successfully. Create a short,
detached exact-commit worktree under `C:\tmp` and run the unchanged
`build/Publish.ps1` from that worktree. Never
install artifacts left in `.staging` by the failed OneDrive-path run.

On managed hosts, NuGet restore and publish commonly fail in the sandbox with
`NU1900` because `https://api.nuget.org/v3/index.json` is unreachable. Retry
the exact command with approved host network access before diagnosing a package
or project failure; do not change package sources or suppress restore errors to
work around the sandbox restriction.

On Windows hosts where local execution policy blocks repository `.ps1` files,
run them in a child process with
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script>`. This bypass
is process-scoped and must not be replaced by a machine policy change. When
running multiple scripts, check and propagate each child exit code; a parent
pipeline can otherwise return success after printing script-load errors, which
is not a valid test result.

The graphical installer success page launches the installed Tray from the
WiX bootstrapper's current user context; the Tray starts the Session Agent.
The ordinary double-click flow keeps both processes at normal integrity, but
an explicitly elevated bootstrapper makes them inherit elevation. Interactive
validation must therefore start the bundle normally, not with **Run as
administrator**. Quiet installs deliberately suppress that UI and launch. A
quiet-install validation must relaunch the installed Tray from its original
normal-integrity parent and must not report success until exactly one Tray and
one Session Agent are running. This caller-controlled relaunch does not solve
the Partner Center-owned Store invocation tracked in issue #25.

## Live tray patching on the installed machine

Use this path only when shared Contracts and both IPC versions are unchanged.
If either changed, do a coordinated full install instead. Do not repeatedly
retry a failed Program Files copy: sandbox escalation is not Windows
administrator elevation.

1. Commit the patch first so the deployed `ProductVersion` contains the exact
   commit hash, then rebuild the tray:

   ```powershell
   dotnet build src\OpenBlade.Tray\OpenBlade.Tray.csproj `
       -c Release -r win-x64 -p:NuGetAudit=false --no-restore
   ```

   The source directory is
   `src\OpenBlade.Tray\bin\Release\net10.0-windows10.0.26100.0\win-x64`.
2. Before stopping either installed process, copy the complete matching tray
   payload into a new staging directory outside `%ProgramFiles%`:
   `OpenBlade.Tray.exe`, `OpenBlade.Tray.dll`, `OpenBlade.Tray.Core.dll`,
   `OpenBlade.Session.dll`, `OpenBlade.Tray.pri`, and `MainWindow.xbf`.
   Verify every staged file exists, is non-empty, and matches the SHA-256 of
   its Release-build source. Do not stage directly into the install directory,
   and do not stop or relaunch anything if this preflight is incomplete.
   Missing `.dll`, `.pri`, or `.xbf` files can make the Session Agent or tray
   exit immediately.
3. Treat `OpenBlade.Tray.Core.dll` and `OpenBlade.Session.dll` as shared,
   process-loaded files. If either differs, stop both the installed tray and
   Session Agent after verifying their executable paths are exactly under
   `%ProgramFiles%\OpenBlade`. Stopping only the tray leaves Tray.Core locked
   by `OpenBlade.SessionAgent.exe`.
4. Use two deployment phases:
   - A normal-integrity parent launches one short-lived PowerShell child with
     `Start-Process -Verb RunAs -Wait`.
   - The elevated child stops the exact installed processes, waits for the
     target files to accept an exclusive read/write open, copies only from the
     verified staging directory, verifies all target hashes, writes a JSON
     report, and exits. It must not launch the tray or Session Agent.
   - After the elevated child exits, the normal-integrity parent must read the
     report and relaunch nothing unless all required target files exist and
     match their staged hashes. On failure, restore the complete previous
     payload or leave both processes stopped; never launch a partial payload.
   - After a successful report, the normal-integrity parent starts the
     unchanged installed Session Agent hidden with `-WorkingDirectory` set to
     the installation directory, confirms that it remains alive with the
     expected shared-DLL versions, then starts the installed tray from that same
     working directory.
5. Do not start the long-lived tray or Session Agent from the elevated child.
   They inherit elevation, Computer Use cannot inspect them normally, and the
   parent `-Wait` can remain blocked on the elevated process tree until those
   applications exit.
6. Before and after copying, record SHA-256, `ProductVersion`, and
   `FileVersion` for at least `OpenBlade.Service.exe` and
   `OpenBlade.SessionAgent.exe`; fail if either protected executable changes.
   Verify every deployed file hash equals its Release-build source.
7. Final live checks:
   - exactly one tray and one Session Agent run from `%ProgramFiles%\OpenBlade`;
   - Computer Use can inspect the tray without a higher-integrity warning;
   - tray and Tray.Core show the intended commit in `ProductVersion`;
   - the service and Session Agent executable hashes match the pre-patch report;
   - a read-only service-status query confirms the changed setting reached the
     authoritative service state. A sandboxed named-pipe probe may return
     `UnauthorizedAccessException`; run that read-only probe outside the
     sandbox after approval instead of changing pipe permissions.

Known failure signatures:

- `Access to ... Program Files\OpenBlade ... is denied`: the copy is not
  elevated. Use the single UAC phase; do not retry the same command.
- `OpenBlade.Tray.Core.dll ... is being used by another process`: the Session
  Agent is still running.
- The wrapper times out but the deployment report is complete: the elevated
  child launched a long-lived process and `-Wait` is following its process
  tree. Correct the two-phase restart; do not assume the copy failed.
- Computer Use reports that OpenBlade has higher Windows integrity: stop the
  elevated tray and Session Agent, then relaunch both from the normal parent.
- The tray starts and immediately exits after a XAML patch: verify the matching
  `.pri` and `MainWindow.xbf` were copied with the binaries.
- Media keys are temporarily dead and Event Viewer reports
  `FileNotFoundException` for `OpenBlade.Tray.Core`: the Session Agent was
  relaunched against a partial payload. Stop relaunch attempts, complete or
  restore the entire staged payload, verify all hashes, and start the Session
  Agent only after the deployment report passes.
