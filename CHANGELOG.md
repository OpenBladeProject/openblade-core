# Changelog

All notable changes to OpenBlade are documented in this file. The project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- Allowed HyperBoost to start from every supported full-power-adapter
  performance profile and fan setting while preserving the capture-backed
  Balanced and Automatic transition baseline, then restored the entry-time
  intent on same-source exits and the destination profile after a power-source
  change, including when fresh readback resolves an indeterminate entry or
  exit, without misreporting the restored manual fan state as an external
  change on the next observation.
- Localized every M1-M5 shortcut action, row label, validation message, and
  accessibility name immediately when the app language changes.
- Moved VBIOS inventory into the Firmware section and now show `None` only
  after Razer's page authoritatively has no exact-GPU publication. Website,
  parser, and installed GPU readback failures remain unavailable and never
  populate installed firmware state. Repeated display-head aliases under one
  Windows video-adapter key now collapse to that adapter's installed VBIOS,
  while zero or multiple NVIDIA adapters still fail closed.
- Placed the Profiles page Import and Export actions beside the title when
  space permits, while preserving accessible ordering and responsive wrapping
  for narrow or long-localized layouts.
- Kept the App theme and App language pickers compact on desktop while allowing
  them to grow for longer localized choices.
- Awaited every supervised Session Agent loop during coordinated shutdown so
  microphone mute indication cannot overlap teardown or lose a late failure.
- Kept completed manual fan selections stable while their authoritative service
  readback catches up, preventing an older in-flight status from briefly
  reverting the fan-mode selector.
- Refreshed the fan-mode selector immediately when HyperBoost activates or
  exits so its enabled state stays synchronized with the authoritative control
  gate.
- Localized the HyperBoost card, its cooling-pad guidance, and its accessibility
  labels in Simplified Chinese and Russian instead of falling back to English.
- Wired the HyperBoost ownership journal into the production Service composition
  so a confirmed activation is durably recorded before lifecycle recovery is
  armed.

### Added

- Added a fail-closed padless HyperBoost admission path on RZ09-0581. The
  reviewed Balanced-and-Automatic protocol, load, adapter, restoration,
  low-power, and unexpected-exit evidence now admits production use without
  the cooling pad. Safety boundaries never retry an indeterminate write.
- Presented padless HyperBoost status in Quick Controls and noted that full
  cooling-pad integration is planned for a future update. The toggle is
  available only when the service reports reviewed write access.
- Added complete Simplified Chinese and Russian translations for the tray,
  graphical installer, and native setup and uninstall experiences. Settings
  now lets the tray follow the Windows UI language or use either translation,
  with safe English fallback.
- Added GPU-specific VBIOS update comparison for exact supported Blade models.
  OpenBlade now carries the installed NVIDIA adapter identity through service
  status and diagnostics, selects only the matching official Razer VBIOS
  release, and declines comparison when either side cannot be identified.
- Added model-scoped RZ09-0581 Thunderbolt 5 and touchpad firmware comparison.
  The service reads the exact Windows USB4 root-router property and PixArt
  Precision Touchpad HID descriptor, fails closed on other models or ambiguous
  identities, and reports updates only from trustworthy installed versions.
- Added exact-RZ09-0581 BIOS 3.01 read-only diagnostics for the captured Synapse
  SKU hardware-configuration byte, Mini-LED panel-resolution byte, and thermal
  fan ID list. The raw values remain in the service's diagnostic cache and
  exports and do not enable firmware writes.
- Added an opt-in **60 Hz lighting** Lab mode for the admitted RZ09-0581
  keyboard transport. The service keeps 30 Hz as the default, persists the
  selection after the tray exits, and applies it to supported built-in,
  Ambient Awareness, and Audio Meter animation while RZ09-0528 remains at
  30 Hz.
- Added en-US resource catalogs and qps-ploc validation for the tray and
  graphical installer, including safe installer fallback and required-key,
  placeholder, accessibility-name, and critical-page startup checks.
- Added an importable example of a cyan sweep that moves from left to right
  with a dim blue wake.
- Added a versioned, data-only `.oblight` keyboard lighting preset format and
  a Lighting-page importer available when Lab features are enabled in Settings.
  Its bounded Razer `.chroma` version 1 adapter
  converts uniform Keyboard and KeyboardExtended files losslessly, and plays
  classic Keyboard per-key animations through separate verified RZ09-0581 and
  RZ09-0528 logical-key projections. Imported animations persist with the power
  profile and keep running when the tray exits. Colored source cells without an
  admitted binding are reported, while per-key KeyboardExtended files are
  rejected instead of cropped. Imported effects remain limited to capabilities
  already admitted for the exact connected Blade.

### Changed

- Replaced the About-page affiliation disclaimer with the translated community
  tagline “For gamers. By the community.”
- Applied tray language changes immediately without restarting OpenBlade and
  aligned the language picker with the other Appearance controls.
- Made production admission model-scoped by default for the exact supported
  laptop and SKU. Validated BIOS versions remain provenance, while explicit
  firmware-specific capability gates can still require exact versions. On the
  RZ09-0581 this intentionally makes the already-admitted 45 W, 60 W, 65 W,
  80 W, 90 W, OEM 280 W, and manifest-backed SlimQ 330 W adapter classes
  available across same-model firmware; their captured BIOS versions remain
  evidence provenance rather than runtime restrictions.
- Advanced service IPC from 47 to 48 for the model-scoped firmware
  compatibility state.
- The firmware comparison table now omits items whose installed version is
  unavailable instead of showing non-actionable rows and a disclaimer.
- The firmware versions table now stays hidden when none of its installed
  values are readable, while update controls and the official support link
  remain available.
- Device Information now omits BIOS, EC, MCU, and VBIOS rows when the same
  installed version is already visible in the firmware comparison table.
- Tied each optional Windows power mode to a laptop performance preset instead
  of an AC, USB-C, or battery profile. Dashboard changes, saved-profile
  transitions, and Fn+P now update Windows only after the laptop preset is
  confirmed. Profile schema 15, export schema 10, and service IPC 47 migrate
  unambiguous existing associations and leave conflicts unchanged. Because
  AC and USB-C commonly shared one laptop preset but different Windows-mode
  choices, existing associations may need to be set again after upgrading. The
  Dashboard selector stays synchronized when the laptop preset changes.
- Advanced the firmware-smoke snapshot schema from 7 to 8 for observed-only
  Thunderbolt and touchpad inventory metadata.
- Expanded the RZ09-0581 BIOS 4.00 USB-C profile to exact 45 W, 60 W, 65 W,
  80 W, 90 W, and 100 W adapter classes. These classes share the same USB-C
  profile; bounded transient HID failures still require two agreeing replies
  before controls are enabled.
- Moved the Lab keyboard preset importer into the Keyboard section and replaced
  the long Lab features description with an on-demand list of current features.
- Advanced the profile/configuration schema from 13 to 14 for the persisted
  lighting refresh-rate preference. Migrated configurations default to 30 Hz.

### Fixed

- Prevented coordinated Session Agent cancellation from becoming a fatal
  named-pipe error that stops function-row and other per-user features.
- Fixed the validation-marked FirmwareSmoke lifecycle bootstrap so it could
  exercise padless HyperBoost while the low-power and unexpected-exit
  production evidence gates were still closed.
- Reworked Simplified Chinese and Russian wording to use natural,
  context-aware terminology throughout the tray, installer, and setup.
- Prevented localized profile-draft captions from overlapping adjacent
  columns at narrow window widths.
- Kept keyboard-lighting effect choices in the same order across supported
  display languages.
- Fixed live language changes mixing strings from the previous language into
  the rebuilt tray when background work retained an older UI culture.
- Prevented nonzero HID firmware metadata from blocking exact RZ09-0581 device
  admission, and corrected the platform warning shown while controls are
  unavailable.
- Preserved responsive selector bindings after Dashboard edits so switching
  between AC and battery cannot leave the laptop-performance dropdown blank or
  prevent it from expanding into horizontal choices when space is available.
- Kept malformed current-schema profile files on the safe recovery and import
  error paths, and cleared stray legacy Windows power modes before publishing
  migrated schema 1 profiles.
- Removed the redundant **Double-click** hint from the tray context menu while
  preserving double-click activation of the main window.
- Removed redundant troubleshooting status dots, clarified the service-start
  action, and removed its idle leading gap.
- Added typed decoding for the capture-backed RZ09-0528 `0011` no-adapter
  response while keeping Windows Offline authoritative for battery power and
  excluding that response from other Blade models.
- Retried transient MCU firmware-version read failures during later verified
  observations, while keeping the query bounded and stopping after a confirmed
  version is available.
- Clarified unclassified external power consistently across control access,
  active profile, tray hover, and troubleshooting instead of presenting a
  completed bounded classification attempt as still checking.
- Kept conditional profile Save and **Use profile settings** actions beside
  Customize when they fit, while moving the complete action group below the
  title or stacking long localized actions before they can overlap or clip.
- Kept page customization buttons aligned with their titles while allowing
  longer localized and profile actions to use a separate row.
- Fixed the **Random colors** switch track alignment and made compact switch
  alignment explicit across the main window and quick controls.
- Expanded the graphical installer's progress details pane to use the available
  window height instead of remaining limited to a few log lines.
- Offered **Repair** and **Uninstall** when an interactive installer finds one
  cached OpenBlade bundle with the same version. Repair overwrites the installed
  files, records instance-specific ownership, and retires the old registration
  without letting it remove the repaired payload. Uninstall keeps saved profiles
  and removes through the cached installed bundle without registering the incoming
  one, with guidance to use its Windows Installed apps entry when profile removal
  is also wanted.
- Advanced the combined service IPC contract to version 46 so peers without
  the expanded firmware inventory are no longer accepted as wire-compatible.
- Rejected imported lighting sequences for a different keyboard layout before
  opening the lighting device or changing its state.
- Kept the Lab features row aligned with neighboring Settings rows when showing
  its information button.
- Validated imported lighting timelines without rebuilding their decoded frame
  graph during each IPC status check.
- Labeled imported animations consistently in profile details and summaries
  instead of showing `Sequence` and `#000000`.
- Preserved configured Windows power modes when migrating schema 12 profiles
  to the keyboard-animation profile schema.
- Kept a completed non-looping RZ09-0528 lighting sequence on its final frame
  without faulting or surrendering the service-owned renderer.
- Prevented brief firmware lighting-off readings during AC and battery
  transitions from being mistaken for external changes. OpenBlade now confirms
  a zero reading at the point where it would release the owned renderer, while
  preserving a stable external lighting change without overwriting it. If that
  confirmation is unavailable, only lighting is skipped; the remaining profile
  controls still apply.

## [0.8.4-nightly.20] - 2026-08-15

### Added

- Added Windows-native display-brightness handling and flyout presentation for
  the reconstructed function row, using the exact admitted Razer virtual
  Consumer Control path already present on the RZ09-0581. The existing WMI
  brightness path and OpenBlade flyout remain available when that path is
  unavailable. About shows which brightness path is currently available.
- Added an optional Windows power mode to each AC, USB-C, and battery profile.
  The current-user Session Agent applies Best power efficiency, Balanced, or
  Best performance when the active power profile changes.
- Added a **Close GPU apps** action to Power mode. It finds current-user apps
  actively using NVIDIA WDDM engines without waking an idle GPU, shows the exact
  targets, asks them to close, and offers an explicit force-close step for
  survivors while protecting Windows, OpenBlade, and other sessions. The action
  stays hidden when no active NVIDIA GPU users are detected.
- Added an administrator-approved Troubleshooting action that stops the exact known
  Synapse and Chroma controller-service allowlist, then reports fresh Windows
  service readback without disabling startup or retrying uncertain requests.

### Changed

- Replaced common dropdown-only settings with responsive Windows selectors.
  Performance, fan, function-row, lighting, display, and app settings use
  horizontal choices when their options fit on one line without squeezing
  adjacent labels, and retain a compact dropdown when the window or settings
  row is too narrow.
- Replaced the tray-dashboard customization dialog with a dedicated Settings
  page that shows only active controls and adjustments, keeps inactive choices
  in **Add** menus, exposes the two-adjustment limit, and updates the pinned tray
  popup live while items are reordered, added, removed, or reset.
- Expanded tray-dashboard quick controls with battery protection, function-row
  mode, gaming mode and its key-blocking options, plus an optional GPU-tuning
  toggle.
- Renamed the tray-dashboard lighting adjustment to **Keyboard brightness** so
  its target is explicit in Settings and accessibility presentation.
- Matched the tray dashboard to the Windows Quick Settings 360 by 388 DIP
  footprint, added configurable power-source and fan-speed status items, and
  standardized its primary icon scale.

### Fixed

- Reduced excess vertical space in single-line Dashboard rows while preserving
  the original spacing for descriptions, editors, and titles that wrap at
  narrow widths without disrupting conditional-row animations.
- Kept HDR toggles made during a background display refresh from being dropped,
  and restored the toggle from confirmed display state after the queued change.
- Stopped the Fixed speed and Manual fan curve detail cards from appearing
  highlighted while idle or when editing, and split shared nested-card styling
  into explicit hover-free editor, neutral hover, and emphasized variants so
  future editors cannot inherit an unintended full-card highlight.
- Made the main window and tray dashboard available before the initial display
  query finishes, while coalescing display refreshes so a slow Session Agent
  response cannot queue redundant follow-up queries. Profile capture still waits
  for a display read that began after the save request.
- Limited the tray-dashboard status row to three configurable readings so its
  icons and labels remain distinct instead of clipping in the fixed-width popup.
- Reduced tray-dashboard background work by sharing one visible-surface
  telemetry loop, reading only the sensors selected for the popup, replacing
  steady foreground polling with bounded deactivation checks, suppressing
  telemetry binding updates for the parked popup, keeping popup animations out
  of EcoQoS, and preventing overlapping animation workers or deferred GPU
  commits during teardown. A bounded UI fallback now also completes dismissals
  if native animation-frame delivery stops early.
- Added a real compiled-XAML startup check for the main window and every tray
  dashboard control, protected newer dashboard preference schemas from being
  overwritten by older builds, and kept GPU slider commits from applying after
  their capabilities or editability change.
- Moved tray-dashboard animation pacing off the shared thread pool, bounded its
  compositor-failure fallback to one frame, and limited tray-click reopen
  suppression to dismissals caused by an actual Windows taskbar.
- Prevented the tray from crashing during startup when loading dashboard status
  icon columns from a numeric XAML sizing resource.
- Replaced the keyboard-brightness adjustment's generic sun icon with the
  Windows keyboard-brightness glyph.
- Kept selected GPU clock adjustments visible in the tray dashboard when GPU
  tuning is temporarily unavailable, showing their disabled state instead of
  silently removing them.
- Kept the pinned tray-dashboard preview above a maximized OpenBlade window
  during Settings customization without making it system-wide always-on-top.
- Kept profile pending-change dots immediately beside their value text instead
  of stretching them to the far edge of the table cell.
- Cleared the red playback-mute key after a rejected unmute frame by restoring
  OpenBlade's owned lighting renderer as soon as the unmuted state is retried.
- Replaced `Not set` laptop-behavior profile cells with the currently confirmed
  firmware value while leaving unconfigured profiles non-destructive.
- Repeated the global charge limit across the AC, USB-C, and Battery profile
  columns so the latter two cells no longer appear empty.
- Disabled the fan-mode dropdown when Automatic is the only available option,
  including on battery and USB-C power, while keeping mismatched selections
  recoverable.

## [0.5.1-nightly.16] - 2026-08-13

### Added

- Added a compact quick-controls dashboard that opens with one click on the
  tray icon. Its tile controls, slider controls, order, visibility, and status
  row can be customized from Settings while the actual popup updates live.
  Open the main window with a tray-icon double-click or the tray icon's
  right-click **Open** command.
- Added production recognition for the SlimQ 330 W adapter on the exact
  Razer Blade 16 RZ09-0581 with BIOS 3.01 or 4.00. The admission is bound to
  ten identical typed adapter reads and four matching Windows online-power
  observations captured on BIOS 4.00; other models and responses remain
  unchanged. The adapter now selects the normal AC profile, including its
  performance mode, fan behavior, and configured GPU clock offsets.
- Added per-power-profile battery protection, including migration from the
  former global setting and pending-value indicators for AC, USB-C, and battery.
- Added the global charge limit to Profiles pending-change presentation.
- Added a dot beside each saved Profiles value that has a pending change.
  The value cell exposes the new value through its tooltip and accessible name.

### Changed

- Refined the dashboard into a Windows Quick Settings-style panel with a
  uniform three-column tile grid, quick actions that cycle through available
  options, chevrons that open focused choice screens, scrolling beyond six
  controls, and a fixed quick-adjustment section for up to two charge-limit,
  lighting-brightness, or GPU-clock sliders. Unused slider space expands the
  tile viewport without changing the popup's overall height. The popup now
  matches Windows' tile, corner, type, and panel proportions; single-choice
  fan controls omit the details chevron, and quick-adjustment rows use larger,
  consistently aligned icons, Windows-sized thumbs and tracks, and no trailing
  labels. Performance and fan controls now use purpose-specific symbols, the
  telemetry footer distributes visible items evenly, and opening the popup no
  longer leaves a focus border on its first tile. Legacy wide-tile and
  expanded-control preferences now migrate to the fixed grid instead of being
  retained as inactive settings.
- Reduced repeated status-publication allocations by reusing immutable drift
  snapshots and validating small sorted IPC collections without LINQ buffers.
- Battery protection and charge-limit changes now remain temporary until saved
  to a profile. Battery protection also follows the destination power profile
  when the power source changes.
- Advanced the product version from the stale 0.1.x baseline to 0.2.0 for the
  backward-compatible capabilities added since the initial non-nightly release
  and required each future product-changing pull request to apply its SemVer bump
  directly, with pull-request fields and CI validation rejecting missing, stale,
  or incorrect increments while keeping description-only rechecks separate from
  the full build workflow.

### Fixed

- Fixed the quick-controls popup's blank black opening frame, raw lighting
  option text, missing fan icon, clipped controls, and truncated status labels.
  The popup now keeps its composed surface prewarmed outside the virtual
  desktop and uses
  matching 200 ms reveal and dismissal motion paced by the desktop compositor
  with the brightness overlay's easing, stays outside the taskbar's topmost
  band, reliably dismisses after an outside click, refreshes its telemetry
  while open, and constrains its tiles to the live viewport at every display
  scale.
- Restored the last confirmed charge limit when a debounced tray edit cannot be
  applied, while preserving a newer edit made during an in-flight request.
- Preserved configured special-key bindings through transient preference-file
  locks and retried the unchanged file after access recovered.
- Reconnected status and lighting streams after temporary capacity rejection,
  kept Ambient Awareness and Audio Meter on one shared frame connection, and
  reserved four named-pipe listeners for ordinary control requests even when
  all supported persistent streams are connected.
- Bounded Core Audio shutdown waits without releasing COM resources still in use,
  and prevented cleared or trimmed input queues from accumulating empty worker
  wakeups.
- Synchronized the microphone-mute key indicator with Windows at Session Agent
  startup and after later mute or default capture-endpoint changes. Playback-mute
  indicator updates now pause while hardware writes are blocked and resume after
  fresh service status.
- Indeterminate hardware writes are now shown as unconfirmed instead of as
  external changes. OpenBlade blocks retries until a read-only current-state
  check confirms the result or identifies a real mismatch.
- Stopped OpenBlade's keyboard renderer from fighting Razer Synapse when
  **Override external lighting changes** is off. OpenBlade now stops its frame
  stream without closing the lighting port or changing keyboard Driver mode,
  keeps a fresh manual lighting action active, preserves recovery state when
  controller detection is temporarily incomplete, and reflects the active
  user's saved toggle after sign-in without attributing warning-only unavailable
  lighting to Synapse.
- Prompted for administrator approval when **Shut down completely** is chosen
  from the normal tray, while keeping the tray and Session Agent unelevated,
  and added a troubleshooting action that starts the stopped service again.
- Prevented the first nightly after a product-version change from replaying
  entries already published under the preceding version when its generated
  changelog pull request is still outstanding.
- Prevented idle NVIDIA GPUs from being woken by clock-offset status reads after
  GPU tuning had already been applied, disabled, or left behind on another
  power profile. Managed offsets are revalidated after resume, while a disabled
  reset is confirmed once without turning unmanaged profile reads into NVIDIA
  polls. Unmanaged transitions intentionally do not wake NVIDIA to reset
  offsets, so driver-held values may remain applied while OpenBlade stops
  reporting them, until tuning is managed again or the driver reloads. A live
  reading outside OpenBlade's admitted range is now reported as unavailable
  instead of being replaced by an older cached value.
- Showed the effective **Off** defaults for Gaming mode's Windows-key and
  Alt+Tab blocks instead of presenting missing profile overrides as **Not set**.
- Fixed `IPC request body is invalid` errors and rejected profile saves after
  editing GPU clock tuning from a legacy profile.
- Made dashboard Power mode subcards neutral at rest while preserving the
  standard hover highlight for GPU tuning and Custom CPU/GPU rows.

## [0.1.0-nightly.11] - 2026-08-08

### Changed

- Simplified the official firmware version card with a clearer **Latest**
  column and a consistent action row for the model's Razer support page.

### Fixed

- Folded GPU clock offsets under their tuning toggle, removed the persistent
  nested-card highlight, and placed dashboard slider values beside their
  sliders to keep the cards compact while retaining their circular thumbs.
- Restored the function-row keyboard-brightness controls on the Razer Blade 16
  RZ09-0581 by advertising the already-admitted brightness command through the
  exact-device lighting capability, and exposed that capability in diagnostic
  archives.
- Kept full-power AC fan controls available in every supported performance
  mode, preserving performance intent through the confirmed Balanced/Automatic
  bridge required for fixed speeds, maximum fans, and manual fan curves.
- Distinguished intentional service stops from unexpected exits with a
  durable run marker. SCM restarts now observe exact-device state before any
  recovery write, retain external-change latches and persistent override
  intent without ordinary profile writes, restore only eligible owned fan and
  lighting producers, and start a manual fan curve at a fresh thermally safe
  target that falls back to maximum speed when CPU telemetry is unavailable.
  Recovery on multi-user installs now waits for the active user's saved intent
  instead of comparing retained hardware state with the machine bootstrap, and
  rechecks session, power, controller, and lighting state before recovery writes.
  A signed-out restart on full AC or battery now establishes one device-admitted
  maximum fan target when retained firmware state is manual, then safely hands
  that owned floor back to Automatic on USB-C without an unbounded retry loop.
  A manual state first observed on USB-C remains unchanged until ownership can
  be established on a compatible source.
  In-process failures still attempt bounded hardware cleanup, while markers
  from an earlier Windows boot no longer suppress ordinary startup profiles.
- Stopped install and repair before Apply when another OpenBlade bundle has the
  same four-part version, preventing cached related bundles from recursively
  removing each other. Setup also rejects machines that already contain
  several related bundle registrations, including mixed older and newer
  versions, instead of launching their legacy installers into the same cycle.
  Interactive setup reports the conflict during detection while retaining an
  available Uninstall escape hatch. Exact development builds must use ordered
  Nightly revisions for transactional replacement.

## [0.1.0-nightly.10] - 2026-08-07

### Added

- Added per-user notification controls for performance and power-profile changes,
  app and Razer firmware updates, controller advisories, and operation failures.
  Repeated toasts are coalesced, update versions notify once, and opening a
  notification routes to the relevant OpenBlade status while durable errors and
  advisories remain available in the app.

### Fixed

- Made function-row brightness failures identify the logical key action,
  recorded sanitized and deduplicated exact-device session failures for every
  lighting open path, and added the active function-key and keyboard
  Driver-mode state to diagnostic archives.
- Kept newly applied Power mode and Custom CPU/GPU selections from briefly
  reverting after the progress spinner finishes while confirmed status catches
  up.
- Aligned MCU versions in the README hardware table with Razer's updater label
  format.
- Dismissed the dashboard warning flyout when its underlying status clears,
  preventing a mostly empty popup after reapplying settings or accepting an
  incompatible firmware override.
- Offered **Use profile settings** when another app changes the keyboard effect
  to a value that firmware cannot read back, allowing the saved profile to be
  restored without guessing the active effect.

## [0.1.0-nightly.9] - 2026-08-06

### Added

- Added Razer Blade 16 RZ09-0581 BIOS 4.00 to the tested production firmware
  set while retaining BIOS 3.01 support and keeping other BIOS versions behind
  the explicit untested-firmware override.
- Added a graphical installer that follows the Windows app theme, keeps a fixed
  compact progress view with expandable wrapped details for file, registry,
  shortcut, and service activity, asks before replacing a newer local version,
  and offers **Launch OpenBlade** after install or repair. It consistently uses
  the installer artwork and **OpenBlade Setup** identity, labels version-removal
  work clearly, avoids stale equal-version replacement cycles, and gives quiet
  and embedded installs a valid native apply owner.

### Fixed

- Fixed the Tray terminating at startup when loading the new GPU tuning card's
  description style.

## [0.1.0-nightly.8] - 2026-08-06

### Changed

- Generalized the developer firmware-smoke workflow for BIOS, EC, MCU, and
  VBIOS updates. Pre-update rehearsals now declare and verify the components
  under test, operator confirmation uses `<MODEL>-FIRMWARE-SMOKE`, and reports
  use `firmware.inventory.available` instead of `firmware.mcu.available`.
- Simplified the tray right-click menu to **Open** and **Exit**. Exiting now
  asks whether to leave keyboard lighting, hotkeys, profiles, and automation
  running in the background or shut down OpenBlade completely.
- Gave the OpenBlade installer a dedicated adaptive file and taskbar icon with
  a Blade-green download badge, while retaining the existing installed-app
  identity.

### Fixed

- Presented the installed MCU firmware consistently in Device information and
  the official firmware card while preserving the raw version for diagnostics
  and compatibility checks.
- Made **Start at sign-in** reflect the effective all-users Tray launcher and
  honor a per-user opt-out while keeping the Session Agent running at sign-in.
- Reported a recognized laptop with incompatible firmware as **Incompatible
  firmware** instead of also showing the contradictory **Laptop not found**
  state, using the service's exact BIOS admission result rather than unrelated
  EC or MCU version notices. Disabling the override now also relinquishes any
  owned reactive or ambient lighting renderer immediately, including after a
  transient post-identity probe failure.
- Tightened the official firmware card into aligned header, interval, version,
  and footer sections, and displayed decoded MCU versions in the same release
  format used by Razer's updater labels.
- Fixed Custom performance controls not appearing until a mode switch when the
  tray starts with Custom mode already active.
- Recorded the combined hardware-cleanup result during every graceful service
  stop so deliberate safety skips remain diagnosable without warning noise.
- Restored firmware-managed Automatic fan control during a graceful service
  stop when the current service instance owns a fixed, maximum, or
  service-rendered fan curve, while retaining the saved fan intent for the next
  service start and leaving first-observed or Synapse-owned fan state unchanged.
- Let a returning console session recover control through safe status polling
  without allowing inactive sessions to invoke hardware observation.
- Kept the service IPC listener alive when a local client connects but stalls
  before sending its first request or receiving a malformed-request response.
- Prevented inactive and Remote Desktop sessions from invoking write-capable
  hardware observation while retaining redacted status and subscriptions.
- Repaired the all-users Tray startup shortcut during every install and repair
  so upgrades cannot leave newly signed-in users without OpenBlade.
- Reapplied the newly active user's keyboard-lighting profile during Fast User
  Switching instead of misclassifying the prior user's lighting as an external
  change, including an A-to-B-to-A round trip.
- Started the visible tray for every signed-in user from the all-users Startup
  folder while safely migrating the installing user's legacy startup link.
- Fixed multi-user builds failing their installer health check because Windows
  named-pipe impersonation ran before the service read the client's request.
- Kept background-user trays connected during Fast User Switching with
  user-specific status fields redacted, restored control without a tray
  restart, authorized subscriptions before consuming a bounded lease, and
  reduced the steady-state request path to one fail-closed console-session
  lookup, with a second check only when committing a user switch. Remote
  Desktop sessions are explicitly read-only.
- Kept the legacy per-user Session Agent startup link until its replacement is
  created successfully, and moved first-use Dynamic Lighting identity
  registration off the Session Agent's startup-critical path.
- Kept applied dashboard Power mode and Custom CPU/GPU selections stable while
  service status catches up, restored the previous selection after real
  failures, and disabled fixed/manual fan choices until the required Balanced
  Automatic baseline is confirmed.

### Added

- Added a separate GPU tuning switch and clock-offset controls for plugged-in
  profiles. Offsets now work with Balanced and other power modes, while
  existing Custom-mode settings migrate automatically. Exact Razer Blade 16
  RZ09-0581 systems use the Synapse-visible policy ranges regardless of BIOS
  version, with NVIDIA capability and readback checks gating every write.
- Added exact-source changelog snapshots to nightly prereleases. After each
  successful publication, the workflow reconstructs any unrecorded nightly
  boundaries and opens or updates a guarded draft documentation pull request
  while preserving changes merged after the build under `Unreleased`. A
  malformed or temporarily inconsistent changelog no longer prevents the
  installer and checksum from being published.
- Added optional Audio Meter smoothing for quieter moments and brief pauses while
  keeping louder responses immediate. Existing profiles keep the current
  unsmoothed behavior;
  service IPC is now version 35, stored profiles use schema 9, and profile
  exports use schema 5.
- Added an explicit per-user firmware compatibility override to the warning
  popup and Settings. The override keeps exact laptop and USB identity checks
  enabled while allowing an untested firmware baseline. Service IPC is now
  version 36.
- Added multi-user Windows support. The LocalSystem service authenticates each
  named-pipe client from its Windows token, permits hardware mutation only from
  the active interactive session, keeps profiles and persistent overrides
  isolated by user SID, migrates the legacy owner's saved configuration, and
  starts the per-session Session Agent for every user. The agent registers its
  Dynamic Lighting identity on first use and relaunches once under that
  identity. Tray and Session Agent endpoints now include the Windows session
  ID, background sessions lose control without exposing another user's
  profiles, service shutdown requires an administrator, and service IPC is now
  version 34.

## [0.1.0-nightly.6] - 2026-08-04

### Added

- Added a read-only firmware assistant for admitted Blade models that shows
  installed BIOS, EC, and MCU values beside versions published on the model's
  official Razer support page without downloading or running firmware tools,
  with a separate user-configurable check interval.
- Added read-only resume validation records to diagnostic bundles. OpenBlade
  now compares confirmed pre-suspend and post-resume control state, names any
  changed subsystems, and does not add validation-driven recovery writes.
  Diagnostics separately disclose the existing owned-lighting restoration and
  whether that lifecycle action applied a control write. Pending validation is
  discarded after one minute or an unrelated post-resume power-source change.

## [0.1.0-nightly.5] - 2026-08-03

### Changed

- Reduced idle CPU wakeups by replacing hidden live-reading polling with a
  rate-limited on-hover refresh, retaining the last reading between hovers,
  and applying EcoQoS plus an efficient processor selection to the hidden tray
  UI thread when Windows reports a hybrid topology. Service and Session Agent
  scheduling remains under Windows control so latency-sensitive hardware and
  input work can use any processor.

### Fixed

- Added a Launch action after successful graphical install or repair so a
  normally started upgrade does not leave the Tray and Session Agent stopped
  until the next sign-in. The launched processes inherit the installer's user
  context. Quiet callers still relaunch the installed Tray explicitly; the
  Partner Center-owned Store path remains tracked in issue #25.
- Made rapid performance-key presses replace the visible Windows notification
  so it immediately reflects the newest selected mode instead of queuing stale
  profile changes.
- Kept manual and automatic update checks nonfatal when the computer is
  offline, with a clear connectivity status and bounded retry delay.
- Fixed the graphical uninstaller so it detects a same-version OpenBlade
  payload and runs the product uninstall before removing its own registration.
- Fixed tray-hover telemetry from waking hidden WinUI presentation work after
  the tooltip was refreshed. Unchanged service metadata now updates only the
  tray label until the dashboard becomes visible again.
- Fixed the RZ09-0581 firmware-smoke capability gate to recognize the admitted
  Audio Meter lighting effect.
- Fixed app accent changes in Light and Dark modes by keeping both WinUI's
  live accent brushes and its theme resource dictionaries synchronized.
- Fixed Ambient Awareness so a failed screen-capture resource is disposed and
  recreated automatically instead of remaining faulted until the Session Agent
  restarts or the selected region changes.
- Fixed M1 and M2 shortcut selection so the action handler uses the binding's
  typed key slot rather than its screen-reader label.
- Fixed normal M5 microphone mute handling after Session Agent startup so the
  Windows mute toggle and the key's red indicator use a shared, collision-free
  Core Audio enumerator activation path.
- Fixed RZ09-0581 M1 and M2 custom shortcuts so the low-level keyboard hook
  dispatches each assigned action before suppressing the accompanying Page Up
  or Page Down key, while retaining Raw Input as a hook-recovery fallback.

### Added

- Licensed OpenBlade under the GNU General Public License version 3 or later.
- Added an uninstall choice to keep saved profiles and settings by default or
  remove them explicitly from the graphical maintenance screen.

- Added a **Reapply OpenBlade settings** action for controls changed outside
  OpenBlade. It resolves the active profile and manual overrides, writes only
  the affected hardware subsystems, and clears each warning only after
  confirmed recovery. When manual fan control prevents safe Power mode
  recovery, it leaves the warning in place and explains how to proceed rather
  than attempting an unvalidated multi-write thermal transition. Service IPC
  is now version 33.
- Added daily and manually dispatched nightly installers from committed `main`,
  with the full CI and production-publish gates, ordered upgrade-safe build
  versions, 14-day artifact retention, GitHub prereleases, SHA-256 metadata,
  and optional GitHub build provenance when enabled on a supported repository.
  The About page can check either the Stable or Nightly release channel.
- Added host-rendered Audio Meter keyboard lighting to the Razer Blade 16
  (2026), `RZ09-0581`.
- Added exact-device keyboard-brightness support for the Razer Blade 16
  `RZ09-0528`, USB `1532:02C6`, on BIOS `2.02`. Admission is limited to the
  independently captured and reversibly validated device paths: AC presets and
  Custom CPU/GPU levels, Automatic/fixed/maximum/host-rendered-curve fans,
  battery protection and 50–80 percent limits, temporary full charge, keyboard
  brightness and Off, logo Off/Static/Breathing, startup animation,
  function-key primary behavior, and Gaming mode. Model-specific CPU Power
  Limit, CPU Voltage Optimizer, and GPU-overclock policy ranges are surfaced as
  observed-only capabilities until safe backends and exact readback are
  validated. Exact PID `02C6` Static and Spectrum are host-rendered over the
  independently captured six-row, 17-column `03/0B` transport and were admitted
  only after a committed exact-build round trip acknowledged every transition,
  independently read brightness, visually confirmed both effects, and restored
  Static at 50 percent. Starlight, Ripple, Tidal, Wave, and Wheel remain
  observed-only exact-model capabilities until their parameters and write paths
  receive the same validation. Windows Dynamic Lighting also exposes
  the exact keyboard as an 85-lamp, virtual-key-aware LampArray. OpenBlade
  offers an ownership control that delegates keyboard lighting to Windows.
  While delegated, every OpenBlade keyboard-lighting producer is blocked in
  the service and the tray links to Windows lighting settings; OpenBlade does
  not register a competing background LampArray provider. The Session Agent
  performs bounded, read-only enumeration
  only after the service verifies the exact tested firmware tuple, and rejects
  any array whose 85-lamp geometry fingerprint differs from captured evidence.
  Its observation source exposes no priority or mutation operation. The
  model's M1–M5 shortcut surface is also retained as observed-only. Exact PID
  `02C6` M1–M5 and Fn+P reports are captured, and a model-aware decoder is
  staged behind the capability gate. Editable bindings stay hidden and the
  decoder remains inactive until a live OpenBlade round trip is validated.
  PID `02C6` M4 is explicitly isolated from PID `02E0`'s conflicting D3
  performance-button interpretation. A bounded exact-device diagnostic also
  records that Windows denies direct shared reads of both keyboard top-level
  collections, preventing an unsafe direct-HID fallback from being mistaken
  for validated special-key access.
  A separate exact-device GPU offset validator is staged behind host, device,
  controller-isolation, and exact-confirmation interlocks; it performs an
  adjacent one-MHz save/set/readback/restore transaction without changing
  production admission until a live round trip succeeds.
  Firmware-smoke schema 4 records these observed capabilities separately so an
  update cannot silently lose them. Diagnostics, About, tested-platform
  assessment, and firmware smoke now derive the RZ09-0528 / PID `02C6` identity
  from the verified model instead of displaying or validating the older
  RZ09-0581 / PID `02E0` target.
- Added exact RZ09-0528 CPU-temperature support using the model-specific
  `\_SB.PCI0.SBRG.EC0.TZRZ` Windows thermal-zone counter. OpenBlade retains the
  earlier RZ09-0581 path as a separate explicit candidate and never substitutes
  an unrelated thermal zone.
- Added GitHub release checks to About with Every startup, Daily, Weekly,
  Monthly, and Manual only intervals. Checks run at most once during tray
  startup when due, and available releases open in the user's browser.
- Added battery health to About using Windows' read-only design and full-charge
  capacity data. If the battery is not ready during service startup, OpenBlade
  retries after the first successful laptop verification and on later
  power-source changes while health remains unavailable. Native capture
  failures are recorded in diagnostics.
- Added a Synapse-style playback mute indicator for the RZ09-0581 keyboard.
  While Windows audio is muted, OpenBlade keeps the F1 mute key red without
  replacing the selected lighting effect. Core Audio notifications update the
  indicator without continuous polling.
- Added an automated MCU firmware smoke-test workflow. It captures a read-only
  baseline and diagnostic archive, compares firmware, capabilities, readbacks,
  telemetry, and installed components after the update, and offers an
  exact-confirmation reversible rehearsal for performance, fans, battery,
  lighting, startup animation, function-key behavior, and Gaming mode.
- Added a tested system-version catalog for the RZ09-0581, BIOS, EC, and MCU.
  The Dashboard now warns when detected versions are unverified and links to
  compatibility details on About without disabling hardware controls.
- Added bounded, redacted persistent logs for non-fatal Tray and Session Agent
  failures. Diagnostic exports now include both user-mode log histories.
- Added a compact, non-activating Windows-style flyout for built-in display
  brightness changes made with the laptop function keys.
- HDR changes now reconcile the native Windows result with an independent
  display-state readback. This handles systems that return an access-denied
  status after applying the requested HDR state, while still failing closed
  when readback disagrees. A bounded developer validator saves, applies,
  reads back, and restores the original HDR state.
- About now lists the installed app, Session Agent, and Service build
  versions, including each component's commit hash.
- Added a session-local function-row bridge for validated RZ09-0581 Driver
  mode. It reconstructs volume, playback, Project, Print Screen, built-in
  display brightness, and keyboard brightness behavior while preserving the
  configured Function keys or Multimedia keys primary layer.
- Added the confirmed keyboard Driver-mode state to Troubleshooting.
- Added NVIDIA GPU core and memory clock offsets to Custom power mode. OpenBlade
  reads the supported ranges from the installed NVIDIA driver, applies changes
  through NVML, confirms readback, and saves explicit offsets in profiles,
  overrides, imports, and exports.
- Added persistent M1 through M5 Fn-layer bindings on the Laptop page. The
  always-running Session Agent can execute keyboard shortcuts, mouse and media
  actions, microphone mute, app or website launch, text entry, and OpenBlade
  power, Gaming mode, or lighting actions while preserving each key's default
  behavior until the user overrides it.
- Added repository guidance for future contributors and AI agents, including a
  codebase map, hardware-safety boundaries, component deployment rules,
  Semantic Versioning, schema and IPC migration policy, and Conventional
  Commit formatting.
- Added local tray crash reports under `%LocalAppData%\OpenBlade\Crashes` with
  version, runtime, process context, exception details, and stack traces. The
  reporter retains the newest 20 files and never suppresses the original
  failure.
- Added the same retained fatal crash reports to the always-running Session
  Agent, including failures from any of its coordinated background loops.
- Added a customizable Dashboard card picker. Users can pin full interactive
  power, fan, battery, keyboard, startup, lighting, and display cards and drag
  them into the preferred order; the selection and order persist in UI
  preferences.
- Added smooth Dashboard card add, remove, and reorder transitions without
  recreating retained controls.
- Added persistent, page-specific section customization to Profiles, Display,
  Laptop, Lighting, Settings, and About. Each page keeps its header fixed while
  users hide or drag its content sections into a preferred order without
  reloading the window.
- Added persistent left-navigation ordering plus app accent choices for Razer
  green, the live Windows accent, and a custom RGB color.
- Added a branded WiX Burn installer with Install, Repair, and Uninstall
  actions plus Windows Installed apps ownership. It delegates system changes
  to the existing transactional setup engine, migrates earlier standalone
  installations without duplicate uninstall entries, and preserves profiles
  by default. The standalone setup remains available for development and
  recovery, including explicit `uninstall --purge`.
- Added Microsoft Store EXE readiness checks for silent offline installation,
  stable Burn upgrades, supported-hardware disclosure, and Authenticode
  inventory. Release publishing now emits a machine-readable report that
  rejects unsigned third-party payloads while first-party signing is deferred,
  and can verify the rendered public privacy policy before submission.
- Added JSON profile import alongside export. Profile files use a versioned
  envelope and schema-specific readers so future releases can add explicit
  migrations without silently reinterpreting older structures.
- Added separate function-key behavior, Gaming mode, and startup-animation
  settings to the AC, USB-C, and Battery profiles. Saved values are restored
  during power-source and laptop lifecycle transitions; older profiles leave
  these controls unchanged until the user explicitly saves them.
- Added per-profile Gaming mode options to block the Windows key and Alt+Tab.
  The always-running Session Agent applies the selected policy only while
  Gaming mode is active.

### Changed

- Clarified the RZ09-0528 CPU Power Limit and CPU Voltage Optimizer cards with
  the exact AMD kernel-driver boundary. The controls remain visible and
  read-only because OpenBlade does not ship a kernel driver or depend on
  Synapse-delivered binaries.
- Fixed graphical-installer upgrades to compare installed versions, pinned the
  Burn upgrade family, added explicit orphaned-marker recovery, and restricted
  installation to Windows 11 version 24H2 x64 to match the tray's declared
  platform.
- Added the signed Microsoft Visual C++ runtime to the offline installer chain
  with version-aware detection that replaces stale 14.x runtimes, and wired
  payload-before-bundle Authenticode signing hooks for public builds.
- Added first-run consent before GitHub update checks, success-based intervals,
  ETag caching, rate-limit backoff, cancellation-safe shutdown, and resilient
  release parsing.
- Advanced the service IPC protocol to version 24 for the battery-health status
  field. Service, Tray, and Session Agent components must be upgraded together
  through the coordinated installer.
- Moved the deferred lighting, charge-limit, and GPU clock commit state out of
  WinUI event handlers into one executable, coalescing Tray.Core policy. Rapid
  edits now consistently keep only the latest value and queue at most one
  follow-up while a hardware write is running.
- Setup rollback now treats restoration of the previous installation as the
  terminal recovery point even if cleanup of a guarded transaction directory
  fails. The cleanup failure remains visible without risking a second move of
  the already-restored installation.
- Changed function-row interception to require a current, confirmed keyboard
  Driver-mode state. Unknown state during service reconnects or firmware
  readback failures now passes keys through and records a diagnostic transition
  instead of relying on a stale Boolean.
- Updated the light and dark tray icon set for clearer taskbar visibility and
  added matching Custom power-mode icons.
- Reduced each host-rendered Ambient Awareness IPC request from thousands of
  bytes to one compact Base64 RGB payload inside the existing typed JSON
  envelope. The Service and protocol codec now share one captured keyboard
  layout mask, and feature-report encoding avoids a redundant buffer
  allocation.
- Made service disconnections observable within two seconds instead of allowing
  a missing named pipe to leave the tray displaying stale connected state.
- Closed the Tray and Session Agent's persistent service connections
  deterministically during process shutdown.
- Made keyboard-brightness keys match Synapse's captured firmware cadence.
  OpenBlade now keeps the exact raw brightness level in the verified lighting
  session and sends one validated `0x10` step per keypress instead of reading
  full service state and reapplying the complete lighting effect each time.
- Separated the non-Fn Game mode, performance mode, and microphone-mute reports
  from the customizable Fn-layer M3, M4, and M5 reports. Their normal actions
  now keep working in keyboard Driver mode without changing Fn-layer bindings.
- Made performance-key feedback immediate. OpenBlade predicts the next admitted
  mode from the latest authoritative service state, shows the Windows notification
  before the firmware write finishes, and follows with a failure notification
  if the service cannot apply the selection.
- Made the tray icon follow the performance mode currently queued by the
  performance key. If the write is rejected or fails, OpenBlade refreshes the
  firmware state before removing the preview so the icon returns to the
  confirmed mode.
- Made About-page action rows fully clickable with a subtle Windows Settings
  hover and pressed highlight. The Troubleshooting expander uses the same
  full-card interaction treatment, and all trailing glyphs share the native
  WinUI chevron size and alignment without separate button outlines.
- Added a target-device Raw Input cross-check for the reconstructed function
  row. If Windows silently removes the session keyboard hook while firmware
  Driver mode remains loaded, OpenBlade completes the current media action and
  reinstalls the missing hook without resetting laptop controls.
- Kept function-row media translation active through transient firmware
  readback gaps by falling back to the active profile's saved function-key
  behavior.
- Reduced foreground CPU use by letting WinUI update only the bindings whose
  underlying values changed instead of invalidating every page binding for each
  service update. Disabled live readings no longer poll the service, telemetry
  changes no longer fan out to unrelated Session Agent features, read-only
  observations reuse recent exact-device admission, and Ambient Awareness
  reduces capture frequency only after the sampled screen remains static.
- Stopped rebuilding the collapsed Dashboard telemetry repeater while another
  page is open or the window is hidden. Tray-hover readings continue updating,
  and the Dashboard refreshes once with the latest values when it becomes
  visible.
- Made Ambient Awareness smoother during motion by matching the 30 FPS matrix
  renderer cadence, then reducing static-screen sampling to 5 FPS after roughly
  half a second without a color change.
- Increased Wave, Wheel, and Tidal playback to 30 FPS by interpolating per-key
  RGB values between their captured source frames while preserving the original
  cycle timing and direction templates.
- Media-key translation now survives transient keyboard Driver-mode read
  failures, recovers one stale HID session per failure streak, and reports an
  unknown Driver-mode state accurately in Troubleshooting.
- Keyboard command reports and animated-lighting matrix reports are now
  serialized so periodic Driver-mode verification cannot collide with a frame
  write.
- Promoted `Use profile settings` from an overflow menu to a page-header action
  on Dashboard, Laptop, Lighting, and Display whenever the current page differs
  from its active profile, including settings changed outside OpenBlade. Page
  header actions now share consistent sizing. Healthy Troubleshooting rows use
  plain status text while warning and error indicators remain.
- GPU clock-only adjustments in Custom mode now bypass redundant Blade firmware
  writes and update only the NVIDIA clock offsets.
- Removed the redundant Reset button from the app accent color setting. OpenBlade
  green remains directly selectable from the accent picker.
- Renamed the default accent's user-facing label from Razer green to OpenBlade
  green. Its `#47E10C` value and serialized preference remain unchanged.
- Moved Display above Laptop in the default left-navigation order and migrated
  installations still using the former default while preserving custom orders.
- Replaced the navigation-order popup with an inline reorderable Settings card
  and made both navigation and taskbar-hover editors collapsed by default.
- Moved power mode, fan controls, and battery controls to the Laptop page while
  keeping the same live controls available as optional Dashboard cards. Profile
  comparison cells now say `Not set` when a profile does not manage a setting.
- Reduced SessionAgent background work by sharing one bounded service-status
  feed across display and lighting features, making idle-lighting checks
  deadline-driven, and removing per-event allocations from the Raw Input path.
- Reduced the self-contained Windows payload by publishing only the Windows App
  SDK components OpenBlade uses and excluding development symbols, unused
  managed satellite assemblies, and unused AI, ML, and Widgets components.
- Persisted the last successfully applied keyboard lighting effect and restore
  it when the service starts. Other Razer software still wins by default; an
  opt-in setting can make OpenBlade reapply external lighting changes.

- Moved protocol capture evidence and capture tooling to the
  `OSSBlade/openblade-captures` repository, included as the `captures`
  submodule.
- Kept Off first in the lighting effect picker and sorted the remaining effects
  alphabetically.
- Updated project, documentation, issue, release-note, and branding links to
  the OSSBlade GitHub organization.
- Aligned the Settings header spacing with the Display and Lighting pages and
  aligned the service and laptop status values in Troubleshooting.
- Advanced the local configuration schema to version 7, profile export schema
  to version 3, persistent override schema to version 4, and service IPC to
  version 19 for GPU clock-offset settings, readback capabilities, and
  keyboard Driver-mode status.

### Fixed

- Fixed shortcut action pickers that changed visually without persisting their
  new binding. Custom RZ09-0581 M1 and M2 actions now also suppress the paired
  native Page Up or Page Down edge for the full key press, while Default and
  unadmitted model paths continue to pass the native key through.
- Hardened keyboard Driver-mode handling around genuine Windows low-power
  transitions. The service now suppresses write-capable periodic and lifecycle
  reconciliation while low power is active, performs the existing guarded
  Normal-mode release before renderer invalidation, and records whether both
  model-specific cleanup paths confirmed, skipped, or could not confirm their
  final mode. Display lighting-policy transitions are also gated at both the
  lifecycle and shared service-runtime boundaries, so Session Agent requests
  can update logical suppression state without performing firmware writes.
  Ordinary display-off notifications do not release Driver mode.
- Kept Audio Meter renderer shutdown serialized with disposal, constrained
  manual fan curves to each laptop's reported RPM range, and made PID `02C6`
  reactive-key pulses safe during concurrent input and lighting-port shutdown.
- Removed the redundant Windows Dynamic Lighting settings link from its
  ownership row.
- Hid Gaming mode key-blocking options while Gaming mode is off.
- Restored the shared pointer-over highlight on settings cards without
  interfering with their nested controls, apply it to expandable Settings
  rows, and clear stale highlights after scrolling, pointer cancellation, or
  pointer-capture changes.
- Constrained every tray page to its vertical ScrollViewer viewport so long
  card messages no longer widen the page and disappear past the right edge at
  compact window sizes.
- Kept temporary display-off lighting suppression from replacing the saved
  lighting effect with Off. Unplugging and immediately closing the lid now
  preserves the destination battery profile's lighting across resume or a
  service restart.
- Prevented asynchronous Settings, display-profile, customization, and accent
  color actions from terminating the tray when an operation fails.
- Fixed page scrollbars remaining visible after scrolling instead of
  auto-hiding.
- Bounded service shutdown while a hardware operation or lighting-port cleanup
  is stalled. OpenBlade now rejects new operations immediately, retains native
  cleanup without force-closing an in-use HID handle, and lets service stop
  continue after the configured drain deadlines.
- Coalesced superseded UI preference saves so rapid changes perform at most one
  in-flight durable write plus one latest-value follow-up, while shutdown still
  flushes the user's final preference state.
- Updated the About page immediately when delayed battery-health data becomes
  available or unavailable without another laptop status change.
- Fixed playback mute indication retries so a rejected matrix write retires its
  faulted renderer and reapplies the latest Windows mute state on a fresh port.
- Retried transient read-only battery-state I/O and timeout failures through a
  fresh identity-verified session before blocking controls. Writes remain
  single-attempt, and indeterminate hardware writes are never retried.
- Standardized the About page's component labels to sentence case.
- Suppressed false Performance-key failure notifications when fresh hardware
  readback confirms that the final selected mode was successfully applied.
- Gaming mode's Windows-key and Alt+Tab toggles now persist immediately to the
  active power profile, so the Session Agent applies their visible state
  without waiting for a separate profile-save action.
- Prevented losing duplicate Session Agent launches from consuming persistent
  log entries before singleton ownership is acquired.
- Coalesced rapid performance-key presses behind a 1.25-second quiet period.
  Notifications still update immediately, but only the final selected mode is
  sent to the firmware instead of queueing every intermediate mode.
- Made the direct microphone-mute key update and verify every distinct Windows
  default capture endpoint instead of only the Multimedia-role device.
- Returned the keyboard firmware to Normal mode when OpenBlade shuts down.
  The service now stops its lighting stream, releases Driver mode with
  independent readback, and only then closes its hardware handles.
- Made F8 and F9 move the built-in display by a visible 10 percentage points
  while still selecting only brightness levels reported by Windows.
- Kept keyboard-brightness writes independent from battery readback failures.
  Lighting still validates its own state before and after every write.
- Fixed the performance shortcut being ignored after OpenBlade detected an
  external performance or fan change. Pressing the dedicated performance key
  or Fn+P now explicitly selects the next mode while preserving unrelated
  external-change warnings.
- Restored the validated firmware Driver-mode lifecycle required by the
  extended Fn layer, performance key, and M1 through M5. OpenBlade verifies the
  mode when opening the device, rechecks it every 15 seconds, and writes only
  after a confirmed fallback to Normal mode.
- Restored media and brightness-key behavior while firmware Driver mode is
  active. Key-down suppression is latched through key-up, continuous controls
  repeat normally, and one-shot actions ignore keyboard auto-repeat.
- Stopped identifying Razer Elevation Service and Razer Game Manager Service 3
  as potential laptop controllers. Synapse and Chroma components that can
  compete for the same controls remain visible as conflicts.
- Fixed performance-key notifications in the self-contained Session Agent by
  including the Microsoft-signed Windows App Runtime notification resource that
  `AppNotificationManager` loads during registration.
- Validated keyboard-shortcut and website values before M1 through M5 override
  their normal behavior. Incomplete or invalid actions now show a validation
  message and leave the physical key's default action intact.
- Kept every option picker selection visible across initialization and status
  refreshes with a shared selection anchor. Equivalent option collections are
  now reused centrally, and conditional Custom-performance and lighting
  direction lists retain stable instances instead of being rebuilt on access.
- Treat known current values for `Not set` profile fields as saveable changes.
  Display, laptop behavior, and lid-logo pages now show the unsaved prompt and
  profile save action, while unavailable or unsupported values remain ignored.
- Made Fn+P performance switching use the Windows P-key scan code observed on
  the target laptop while continuing to track the separate firmware Fn report.
- Kept tray hover telemetry updating while the main window is hidden, and
  request only the sensors actually shown by the Dashboard or hover label.
  OpenBlade now checks Windows' existing GPU-engine activity first in every
  profile, initializes NVML only while the NVIDIA GPU is active, and omits
  missing or powered-down GPU temperatures from the UI.
- Bound every duplicated Dashboard control card directly to its live view
  model, preventing Dashboard instances from retaining stale visibility,
  availability, selection, or loading state after their original page updates.
- Kept the Power mode, Fans, and Battery controls visible and interactive while
  OpenBlade retains the last confirmed AC or USB-C classification during a
  transient adapter read. Each requested change still performs a fresh adapter
  and exact-device check before writing. Reordering the selected Settings page
  also no longer shows Dashboard content beneath the Settings navigation item.
- Kept shared Dashboard card controls and their original Laptop, Lighting, and
  Display page instances synchronized after service initialization, preventing
  source-page controls from remaining disabled while the same Dashboard card
  is available.
- Prevented the Dashboard customizer from crashing WinUI when the default card
  is unchecked. Edits are staged until Done and applied in place using
  independent instances of shared live card templates, without reparenting
  loaded controls or restarting the tray. Selection controls also align with
  the dialog's right content edge.
- Restored the visibility-optimized outlined application icon so the taskbar
  glyph remains legible against a dark Windows taskbar.
- Made the approved Tidal and Wave/Wheel oracle fixtures check out with their
  required CRLF byte representation so their production hash gates and semantic
  fingerprint also pass in fresh clones.
- Prevented closed information banners from reserving empty vertical space,
  keeping the first section gap consistent across Dashboard, Lighting,
  Display, and Settings.
- Sorted the Lighting page's effect picker alphabetically by its displayed
  effect names.
- Collapsed the dashboard's combined fan reading to one `Unavailable` label
  when neither fan sensor has a value.
- Kept OpenBlade's selected lighting effect visible while a write is pending
  instead of replacing it with the external-effect placeholder after a periodic
  firmware observation that cannot read the matrix effect.
- Made OpenBlade green (`#47E10C`) the default and recovery color for lighting
  effects when no explicit or confirmed color is available.
- Kept advisory detections of other Razer software from blocking Reactive and
  Ripple key input after OpenBlade has verified the laptop and owns the renderer.
- Made dashboard telemetry columns resize within the hero card instead of
  clipping the final reading, and kept all three groups on one row at the
  minimum window width by distributing the available space between them.
- Switched the profile comparison to its compact presentation sooner and moved
  pending-change labels below profile names so header badges cannot overlap the
  neighboring profile column.
- Used the same 50% visible-brightness fallback in shared profile and override
  recovery that the Lighting page uses for unconfirmed effects, preventing
  legacy zero-brightness visible effects from silently restoring at 100%.
- Kept firmware-backed laptop behavior controls on their last known selection
  while readback is loading, disabled them during the load, and removed a
  mojibaked function-key fallback label.
- Retained the last connected dashboard state during brief service reconnects
  so short named-pipe rotations do not flash controls unavailable.
- Made dashboard live readings use consistent separators, collapse fully
  unavailable temperatures to one label, and avoid trailing or outer telemetry
  footer margins.
- Added a telemetry-only observation path between ten-second full-state audits,
  avoiding repeated capability, firmware, and control-state reads on every
  visible refresh while still detecting settings changed elsewhere.
- Applied separate bounded deadlines to display-agent connection, query,
  mutation, and response phases, and contained client disconnects or stalls to
  one connection so they cannot stop the other session-agent features.
- Enforced an 800 by 600 DIP minimum window size without exceeding the current
  display's usable work area at high scaling.
- Reused a very recent verified device admission across transient HID probe
  failures so brief enumeration or open errors do not flash the laptop as
  missing while later reads and writes still perform exact-device checks.
- Serialized Reactive and Ripple failure-state publication with normal service
  operations so concurrent status revisions cannot overwrite each other.
- Rejected non-terminal SetupAPI enumeration failures and invalid HID detail
  sizes instead of treating a partial device list as complete.
- Reused one ordered, idle-bounded IPC connection for Reactive and Ripple key
  bursts, discarded canceled exchanges before reuse, and kept the session Raw
  Input listener alive through transient status-stream failures.
- Retried accepted Reactive and Ripple key pulses across brief service-send
  failures instead of dropping the first pulse after a transient reconnect or
  HID stall.
- Preserved a healthy owned lighting renderer across temporary firmware-read
  failures so polling cannot flash the laptop unavailable or interrupt
  key-triggered effects.
- Increased host-rendered fade cadence to 30 frames per second without changing
  captured effect durations, and initialized unknown visible brightness at 50%.
- Bounded session-agent display IPC reads and contained stalled or truncated
  frames to the individual client connection.
- Retained accepted Reactive and Ripple key pulses through brief IPC and HID
  stalls instead of canceling them after 100 ms, while keeping the queue bounded.
- Preserved persistent fixed-speed and manual-curve fan details across service
  restarts with a schema-three override migration.
- Rejected semantically invalid persistent lighting overrides during recovery
  instead of retrying unusable settings after every startup.
- Refreshed the UI when only fan-control details or lighting-renderer ownership
  changes, rather than treating those events as telemetry-only updates.
- Made concurrent lighting invalidation and disposal callers await the same
  hardware cleanup instead of returning before renderer ownership is released.
- Kept failed native HID cancellation from blocking the serialized hardware
  session indefinitely while retaining pinned resources for a late callback.
- Added a two-second service request-read deadline so incomplete local IPC
  frames cannot indefinitely occupy every named-pipe listener.
- Preserved exclusive ownership of the service pipe from startup through
  connection handoff so another local process cannot claim its fixed name.
- Pinned setup service-control operations to the Windows System32 `sc.exe`
  instead of allowing executable search-path resolution.
- Applied and verified the protected machine-data ACL on existing child files
  and directories instead of securing only the parent directory.
- Prevented interactive keyboard pulses from waiting behind periodic firmware
  observations or repeating exact-device enumeration for every key, and
  preserved status through routine subscription rotation.
- Preserved repeated key-triggered lighting pulses, kept Raw Input active across
  brief service reconnects, and refreshed cached input-device identity after
  device changes or transient lookup failures.
- Kept one transient verified-device access failure from demoting every control
  while retaining fail-closed handling after attempted or indeterminate writes.
- Preserved last-confirmed immutable capabilities across a failed refresh and
  reduced high-frequency controller and external-power discovery work without
  weakening the checks performed before mutations.
- Reused one ordered Ambient Awareness connection and the already-admitted
  exact-device lighting port instead of reopening IPC and rediscovering HID for
  every 20 Hz frame.
- Made dashboard telemetry rows content-sized so unavailable temperature values
  wrap without clipping or leaving excess card space.
- Defaulted an unconfirmed visible lighting effect to 50% brightness in both the
  editor and the hardware request instead of displaying 0% while firmware used
  a different value.
- Applied the OpenBlade application icon to every executable project.
- Removed the glitchy long-distance navigation selection-indicator animation.
- Enforced an 800 by 600 DIP minimum app-window size to protect responsive
  layouts.

### Added

- Removed the optional UIAccess input companion from the MVP. Interactive
  lighting and performance-shortcut input remain supported in normal user apps,
  while elevated applications are treated as an explicit Windows input-boundary
  limitation.
- JSON export for all saved AC, USB-C, and battery profiles from the Profiles
  page, including a portable schema version, OpenBlade version, and export time.
- Short height-and-opacity transitions for conditional Power mode and fan-control
  subcards, with no reserved space after a card collapses.
- Green accent dots on navigation items with unsaved profile changes.
- Adjustable Fixed Fan Speed from 2,000 to 5,400 RPM in captured 100 RPM
  steps, plus a native slider that reflects the active target.
- CPU and GPU manual fan-curve editing, an optional hotter-sensor policy, and
  a service-owned one-second control loop. Curve writes stop if another app
  exits firmware manual mode, and missing or critical temperature data forces
  the captured 5,400 RPM fail-safe target.
- Exact-device manual curves use the firmware-defined Razer `TZRZ` ACPI thermal
  zone through Windows PDH for the CPU-side temperature and signed NVIDIA NVML
  for GPU temperature, without installing or redistributing a sensor driver.
- Sanitized Synapse oracle evidence proving that Manual Fan Curve is a
  host-calculated pair of independent per-fan RPM writes rather than a
  firmware-stored curve table.

- Exact-device Function-key behavior readback so Settings can show and verify
  the current Function keys or Media keys state.
- Exact-device Gaming Mode readback so Settings can show and verify the current
  firmware-facing On or Off state without implying Synapse software policy.

- Settings now offers a confirmed full shutdown that stops the tray,
  SessionAgent, and LocalSystem service without launching a shell command.
- A lightweight per-user SessionAgent that exclusively owns Windows display
  queries and changes while the LocalSystem service remains the hardware and
  profile authority.
- Built-in display controls for driver-enumerated fixed refresh rates, EDID-reported Windows Dynamic Refresh Rate, HDR, and the locally installed Razer color profiles.
- Keyboard lighting controls for Off, Static, Spectrum Cycling, single/dual/random Breathing, Reactive Short/Medium/Long, color, and brightness on the exact RZ09-0581/PID 02E0 device.
- Capture-faithful Tidal keyboard lighting with fixed or random two-color
  palettes and independent leftward/rightward direction controls.
- Host-rendered Starlight keyboard lighting with one color, two colors, or
  random colors and independent Short, Medium, and Long twinkle durations.
- Capture-faithful Wave and Wheel keyboard lighting. Wave supports leftward and
  rightward motion; Wheel supports clockwise and counterclockwise rotation.
- Capture-calibrated Ripple keyboard lighting with a selected color and
  per-key expanding rings driven by the existing data-minimized Raw Input path.
- Typed laptop-logo lighting controls for Static, Breathing, and Off with
  independent state/effect readback and rollback.
- Data-minimized Reactive input through Windows Raw Input: scan-code make events map directly to matrix slots without recording characters, virtual-key values, applications, window titles, or input packets.
- Sanitized Synapse oracle USBPcap evidence for lighting brightness readback, the 102-slot matrix envelope, effect timing, dual/random Breathing, and Reactive key placement and decay. The admitted configuration was also exercised in repeated installed OpenBlade runs on the target laptop.
- Sanitized Synapse oracle evidence for all five Ambient Awareness screen
  regions: Full, Left, Top, Right, and Bottom. The capture confirms that region
  selection is host-side screen sampling followed by the shared matrix stream,
  not a firmware region command.
- Ambient Awareness with Full screen, Left, Top, Right, and Bottom sampling.
  The signed-in SessionAgent downsamples directly to the 17 by 6 keyboard grid;
  only typed RGB colors cross IPC, and sampling stops when the effect is no
  longer active.
- A sanitized, hash-pinned five-second Tidal matrix oracle for both directions;
  raw USB captures remain local and are not distributed.
- A sanitized, hash-pinned one-second Wave and Wheel matrix oracle covering all
  four captured direction variants.
- Keyboard lighting power policies that switch lighting off while the display
  is off or the signed-in user is idle for 1 through 60 minutes, then restore
  the current profile or manual override after every suppression reason clears.
- Performance-button and Fn+P support from the captured MI_01 Raw Input reports.
  Plugged-in power cycles Balanced, Silent, Performance, and Custom; battery
  cycles Balanced and Battery Saver; USB-C remains Balanced.
- Native Windows notifications after a physical performance-button or Fn+P
  shortcut successfully applies a new mode. Profile changes, startup, resume,
  polling, blocked shortcuts, and failed shortcuts do not generate a success
  notification.
- The bounded read-only performance discriminator probe now includes fixed
  `0D/87` CPU and GPU component queries. Target-hardware readback confirmed the
  saved CPU Medium and GPU High values, allowing Custom mode to expose the
  captured Low, Medium, and High component choices while blocking High/High.
- Exact-device Startup animation control with validated read-only `0F/98`
  state, based on isolated On, Off, and restored-On responses. Function-key
  behavior uses validated read-only `02/86` state, and the firmware-facing
  Gaming Mode bit uses validated read-only `00/88` state.
- A sanitized Synapse protocol-logger and persistence audit proving that
  Function key behavior and Gaming Mode are displayed from Synapse-owned saved
  state and replayed through setters, not hydrated from observed firmware
  getters. The investigation restored both original selections and sent no
  guessed commands.
- Developer-only, exact-device candidate probes for the original Function-key
  and Gaming Mode getter hypotheses. Each named command has one compile-time
  request, strict RZ09-0581/PID 02E0/BIOS 3.01 and controller gates, and one
  exchange with no retry. Both readbacks were subsequently discriminated across
  known states and admitted to production with strict payload decoding.
- A captured-feature audit that maps every reproducible target-device operation
  to its production surface and records why Trackpad, Snap Tap, and
  uncaptured effects remain hidden instead of relying on guessed commands.

### Changed

- Reactive and Ripple key presses now wake the existing matrix stream instead
  of submitting competing feature-report writes. The service also avoids a
  redundant renderer-ownership read for every key, reducing intermittent input
  latency and animation-frame contention.

- Direct Fixed Fan Speed and Manual Fan Curve changes now accept the generic
  manual firmware readback used by PID 02E0, while entering manual control
  still requires a confirmed Balanced and Automatic state.
- Reactive and Ripple input now queues accepted keys for one persistent renderer
  and timestamps each pulse when the renderer can consume it. Animated effects
  use deadline-based scheduling so HID write time no longer accumulates as
  visible frame drift.
- Firmware controls consistently reserve an inline progress indicator while a
  read or write is pending, and page navigation marks the specific page with
  unsaved changes.
- Lighting color rows now stay compact; the native picker flyout contains the
  color channels and hex input. Tidal directions are labeled Outward and Inward
  to match the captured keyboard motion.
- A transient device-admission miss during a scoped lighting action no longer
  replaces the last verified laptop status with a global unavailable state.
- Dashboard fan telemetry now presents the two channels as a clear
  `Fan 1 / Fan 2` pair, profile comparisons name lighting triggers and outcomes
  explicitly, and the taskbar hover list can omit the OpenBlade app name.
- Fan mode changes remain visibly pending with a progress ring, and direct
  Fixed Fan Speed to Manual Fan Curve transitions re-arm the captured manual
  state before applying the new target.

- Dashboard live readings now use three compact semantic groups for Battery,
  Temperatures, and Fans while retaining individual visibility controls.
- Taskbar hover content exposes Power source and Power mode as separate,
  reorderable items instead of combining AC or Battery with the active mode.
- Lighting display-off and inactivity rules are now saved independently in the
  AC, USB-C, and Battery profiles. Schema 4 global rules migrate into all three
  profiles when first loaded.
- Switching directly between Fixed, Manual fan curve, and Maximum now performs
  the firmware-required Automatic transition internally without changing the
  user-visible selection.
- The application accent palette now uses `#47E10C` as its Razer-green base.
- Dashboard colors now have one semantic role each: accent marks the active
  profile and primary action, green marks readiness, amber marks attention,
  red marks failure, and ordinary power-source and unsaved-edit labels remain
  neutral. Status pills share the same foreground, sizing, and container style;
  profile drafts use neutral text instead of accent dots, and transient checks
  use progress indicators instead of looking like another status category.
- Custom CPU/GPU controls and Fixed/Manual Curve fan controls now expand as
  full-width child rows beneath their Power mode and Fans parent rows, matching
  the visual hierarchy used by Windows Settings.
- Dashboard live readings now include the safe CPU-side firmware temperature
  and NVIDIA GPU temperature. Settings can independently show or hide each
  temperature, fan, and battery-power item without disabling collection.
- Taskbar hover content is now a checked, draggable list, allowing users to
  choose and order the active profile, net battery power, temperatures, and
  individual fan speeds within the Windows tooltip length limit.
- The UI uses OpenBlade green for its WinUI accent while reserving green, amber,
  and red status colors for ready, attention, and failure states.
- Lighting edits apply automatically after a short debounce instead of requiring
  a separate Apply button, and newer edits are not cleared by an older in-flight
  request.
- Fan-curve slider tracks stretch across their child panels and update their RPM
  labels as values are dragged.
- The full-shutdown action uses the Windows critical-action color and keeps its
  existing confirmation dialog.
- Profile comparison now includes laptop lid-logo behavior and the global rules
  that switch keyboard lighting off with the display or after inactivity.
- Explicit Fixed, Manual Curve, and Maximum fan actions now always resubmit the
  requested RPM target even when firmware already reports its undifferentiated
  manual state. This also allows changing manual targets without returning to
  Automatic first.
- Laptop-logo controls now initialize from firmware readback and no longer offer
  a misleading Leave unchanged choice. When a keyboard effect was selected by
  another app and cannot be queried, OpenBlade reports that limitation instead
  of falsely labeling the effect Off.
- Lighting power-policy switches now use the same compact alignment as other
  settings, and the inactivity value is vertically centered in its NumberBox.
- Gaming Mode and Startup animation now use native right-aligned toggles instead
  of dropdown menus, while retaining confirmed firmware readback.
- Self-contained WinUI publishing now includes compiled XAML resources;
  incomplete payloads fail validation instead of crashing at startup.

- Laptop-logo lighting now has its own section on the Lighting page. Firmware-only
  Function key behavior, Gaming Mode, and Startup animation actions are grouped
  under Laptop behavior and use explicit Set menus instead of implying that
  OpenBlade can query their current values.
- User-facing power-source labels consistently use AC instead of mixing AC with
  Plugged in.
- Dashboard, Lighting, and Display now present pending profile saves beside the
  page title instead of as settings-card rows. The compact action names the
  destination profile, keeps restoration in an overflow menu, and moves below
  the title in narrow windows.
- Lighting remains available in navigation when its device interface cannot be
  accessed. The page shows the configured values in a disabled editor and
  explains whether the service, laptop detection, or another Razer app is
  preventing control.
- Status subscriptions now reserve enough bounded pipe capacity for the Tray,
  all three SessionAgent observers, and concurrent short control or diagnostic
  requests, preventing a healthy service from appearing unavailable in the UI.
- Exact-build lighting validation now covers fixed and random Tidal in both
  directions, every Starlight color and duration variant, both Wave directions,
  both Wheel rotations, plus laptop-logo Breathing, Off, and Static restoration
  before the production control surface can be admitted.

- About now uses the canonical 256 px OpenBlade artwork, groups app and service
  builds in a dedicated Versions card, integrates the independent-project
  disclosure into the title card, and uses borderless Windows-style action
  icons.
- Profiles can capture Windows' current refresh rate, HDR state, and color
  profile into the active power profile before saving the draft.
- The Troubleshooting header now aligns with the other About card content.
- Named-pipe protocol version 14 carries typed Custom CPU/GPU settings while
  retaining one-shot laptop controls, lighting suppression, performance
  shortcuts, and the installing-user-only full shutdown request.
- Profile schema version 4 stores Custom CPU/GPU settings and migrates schema
  version 3 profiles with the captured Medium/High default. Schema version 3
  stores the global lighting power policy and remains migratable.
- The tray icon now follows the effective active profile when firmware readback is
  unavailable, instead of showing a generic error while the service is ready.
- Updated the runtime application icon from the canonical OpenBlade branding
  repository, including its continuous notch-free display bezel.
- Selecting a visible keyboard effect from Off now starts with a visible color
  and nonzero brightness. Legacy non-Off settings saved at 0% are migrated, and
  0% is reserved for Off so Breathing cannot be accepted and then disappear.
- A transient keyboard renderer failure now gets one bounded automatic restart,
  including while the tray is hidden. Persistent failures and settings changed
  elsewhere remain stopped and are reported without a retry loop.
- Keyboard colors now use a native color picker with optional advanced RGB
  fields, and lighting overrides are summarized as readable effect details.
- The dashboard falls back to the active USB-C profile when firmware reports a
  thermal value that is invalid for USB-C, avoiding blank or contradictory
  performance and fan selections.
- Added a per-user Start minimized option that keeps the app in the notification
  area on initial launch while preserving normal tray and second-launch behavior.
- Status pills now use the same compact dot indicator for readiness and attention
  states.
- The external-change pill now says "Changed outside OpenBlade" and clarifies
  that saved profiles are unchanged, keeping it distinct from an unsaved
  active-profile draft.
- Dashboard fields now collapse to the single label "Unavailable" when the
  service is offline instead of repeating subsystem-specific explanations.
- User-facing setting labels now call the performance category "Power mode,"
  avoiding a duplicate "Performance" label when the Performance mode is selected.
- The Profiles page now uses an adaptive comparison matrix for power, cooling,
  lighting, and display settings, with a single-profile detail view in narrow windows.
- Display status now identifies the panel, resolution, active refresh mode, color
  depth, HDR state, and color profile instead of showing a generic detection message.
- Profile headers keep the Active badge beside the power source, while troubleshooting
  uses concise AC, USB-C, Battery, and None values.
- A new About footer page now groups app and service versions, supported-device and
  firmware details, diagnostics, help links, live troubleshooting, and project attribution.
  Settings now contains preferences only, and the Dashboard status pill opens About.
- Reactive input no longer emits a matrix pulse for the unlit space bar.

- Display profile automation now remains active after Tray Exit. The first
  confirmed source is observation-only; later real AC, USB-C, or battery
  transitions apply the destination profile through one serialized agent queue.
- Display automation remembers the last successfully reconciled power source so
  a SessionAgent restart catches up a source change it missed without
  reasserting settings when the source is unchanged. A transient Windows
  display failure receives one bounded retry on the next service status update.
- The SessionAgent starts independently at sign-in, while the existing
  Start-at-sign-in setting continues to control only the visible tray.
- Replaced the page-level legacy `ScrollViewer` controls with InteractionTracker-based `ScrollView` controls for smoother high-refresh-rate scrolling.
- Display polling and mutation readback now run Windows topology, registry, and ICC work off the UI thread; unchanged option collections are reused and background checks no longer disable controls, removing a confirmed periodic high-refresh jank path.
- A transient adapter recheck that retains the last confirmed power source no longer flashes the dashboard as `Controls unavailable`; hardware writes remain blocked until classification recovers.
- Refresh-rate choices now match the modes Windows exposes for the internal panel: 60 Hz, 240 Hz, and Dynamic 48-240 Hz.
- Reactive decay now matches captured Synapse timing: 0.5 seconds Short, 1.5 seconds Medium, and 2.0 seconds Long.
- Spectrum Cycling now follows the measured 37.71-second Synapse RGB curve, including its blue hold, instead of approximating it as a 40-second HSV wheel. Random Breathing now selects independently from the captured 24-color palette and permits consecutive repeats.
- Named-pipe protocol version 9 reports explicit host-renderer ownership in addition to the typed, bounded Reactive key-pulse operation introduced in version 8.
- Performance, fan, lighting, and built-in display choices now form one profile per power source. Changes apply immediately to the active profile, remain a compact unsaved draft until saved, and are discarded on a power-source transition.
- The Profiles page previews pending values directly in the affected profile card,
  marks that card with an unsaved dot, and keeps Save and Discard in one compact
  action row.
- Removed the obsolete hidden manual-change-duration preference. Active-page
  changes are always scoped to the current power source until saved as a profile.
- Profile save and discard now remain truthful when temporary-override cleanup fails or a previously unconfigured display profile must return to its pre-edit state.

### Safety

- Transient unknown power readings and service reconnects block display writes
  without discarding the last confirmed source baseline. Same-source profile
  saves and external display changes do not trigger automatic reapplication.
- Display settings are applied only on direct user action or a confirmed power-profile transition, then validated and read back through Windows. Periodic observation reports changes made elsewhere without reasserting them.
- SDR color profiles are unavailable while HDR is enabled.
- Color-profile selection now activates and verifies the current user's Windows profile scope before applying the panel's MHC2 calibration.
- The Display page observes changes made by Windows or other software without reasserting OpenBlade values, and pauses observation while a picker is open.
- Transient USB-C adapter-classification failures retain the last confirmed power-source label while blocking hardware writes until classification recovers.
- Lighting writes remain fail-closed unless the exact model, PID, BIOS, typed effect parameters, pinned oracle evidence, acknowledged transition handshake, and bounded 102-slot matrix report match. The admitted effect set passed the exact-build workflow and repeated installed use on the target laptop.
- Production lighting admission now fingerprints the reviewed lighting transport
  and renderer sources. Unrelated UI, service, and display commits retain the
  admitted command set, while a semantic lighting implementation change fails
  closed until its new fingerprint is deliberately reviewed and approved.
- Reactive keyboard observation exists only while accepted Reactive lighting is active; the production lighting port independently rejects pulses unless its live renderer owns Reactive. Unsupported and extended keys are ignored, and repeated make events are delivered sequentially through a bounded queue.
- Reactive input, renderer-stream, and rollback failures are surfaced without success notifications or per-key message spam. Partial lighting rollback is classified as indeterminate instead of claiming that the prior state was restored.
- Suspend, resume, removal, and reconnect drain renderer ownership and restore it once only when OpenBlade owned the prior effect. Automatic power transitions preserve unknown external lighting, while a transition from an OpenBlade-owned effect applies the destination profile.
- Brightness changes made by another controller are observed through independent readback. Matrix effects have no firmware GET command, so concurrent effect changes remain visible through the existing Razer-app coexistence warning rather than being claimed as directly detectable.

## [0.1.0] - 2026-07-17

### Added

- A native Windows 11 WinUI 3 tray application backed by a LocalSystem service and versioned named-pipe contracts.
- Automatic profiles for the validated 280 W adapter, 100 W USB-C PD operation, and battery operation on the 2026 Razer Blade 16 (RZ09-0581, VID 1532, PID 02E0, BIOS 3.01).
- Admitted performance modes for each power source, automatic fan control, the validated Maximum fan sequence on full-power AC, and independent subsystem result reporting.
- Battery protection with a 50-80% charge-limit slider and a temporary charge-to-100% override that restores the configured limit after unplugging.
- Optional fan-speed and net battery-power readings with a configurable refresh interval.
- A Windows Settings-inspired light, dark, and system-following interface with Mica, tray shortcuts, profile editing, and a customizable hover label.
- Troubleshooting and redacted diagnostic export covering laptop identity, BIOS, EC, MCU, VBIOS, UI and service versions, connection state, capabilities, and command results.
- Product versions that include the source commit hash for reproducible diagnostics.
- Transactional install, update, and uninstall support for the LocalSystem service and unelevated tray application.

### Changed

- Renamed the project to OpenBlade to reflect its Razer Blade-only scope.
- Other Razer software is treated as a coexistence warning instead of being stopped automatically or making all controls read-only.
- Settings changed by another controller are observed and reported instead of being immediately overwritten.
- The core repository includes only the application and tray icon binaries needed at runtime. Brand source files, exports, and unused variants remain in the separate branding repository.

### Safety

- Hardware writes fail closed unless the connected laptop, product ID, command, and value are explicitly supported.
- The service serializes hardware access and exposes typed operations only; there is no raw-command IPC endpoint.
- OpenBlade uses the standard Windows HID interface and does not install a kernel driver or depend on Synapse components.
- Captured protocol data committed to the repository is sanitized; raw packet captures remain excluded.

### Known limitations

- Version 0.1.0 supports only the Razer Blade 16 RZ09-0581 / PID 02E0 on BIOS 3.01 and Windows 11 x64.
- Sensor coverage is intentionally limited to fan speed and net battery power.
- Reactive oracle captures cover alphanumeric anchors, punctuation, modifiers, arrows, F1-F12, Delete, Fn, and the Fn+F12 Print Screen chord. The installed Raw Input path was confirmed with the A key; remaining mapped positions are backed by the captured matrix layout and focused automated tests. The unlit space bar is deliberately ignored, the power button was excluded, and no numpad is present on this keyboard.
- Fan control provides Automatic and validated Maximum modes, not custom fan curves.
- Unrecognized external adapters remain read-only.
- Unsupported or unverified controls remain unavailable rather than attempting arbitrary firmware commands.

[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.8.4-nightly.20...HEAD
[0.8.4-nightly.20]: https://github.com/OSSBlade/openblade-core/compare/v0.5.1-nightly.16...v0.8.4-nightly.20
[0.5.1-nightly.16]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.11...v0.5.1-nightly.16
[0.1.0-nightly.11]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.10...v0.1.0-nightly.11
[0.1.0-nightly.10]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.9...v0.1.0-nightly.10
[0.1.0-nightly.9]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.8...v0.1.0-nightly.9
[0.1.0-nightly.8]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.6...v0.1.0-nightly.8
[0.1.0-nightly.6]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.5...v0.1.0-nightly.6
[0.1.0-nightly.5]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0...v0.1.0-nightly.5
[0.1.0]: https://github.com/OSSBlade/openblade-core/releases/tag/v0.1.0
