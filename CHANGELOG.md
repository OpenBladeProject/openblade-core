# Changelog

All notable changes to OpenBlade are documented in this file. The project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Adjustable Fixed Fan Speed from 2,000 to 5,400 RPM in captured 100 RPM
  steps, plus a native slider that reflects the active target.
- CPU and GPU manual fan-curve editing, an optional hotter-sensor policy, and
  a service-owned one-second control loop. Curve writes stop if another app
  exits firmware manual mode, and missing or critical temperature data forces
  the captured 5,400 RPM fail-safe target.
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

- Dashboard colors now have one semantic role each: accent marks the active
  profile and primary action, green marks readiness, amber marks attention,
  red marks failure, and ordinary power-source and unsaved-edit labels remain
  neutral. Status pills share the same foreground, sizing, and container style.
- Custom CPU/GPU controls and Fixed/Manual Curve fan controls are inset beneath
  their Power mode and Fans parent rows, respectively.
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

[Unreleased]: https://github.com/boyuanx/openblade-core/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/boyuanx/openblade-core/releases/tag/v0.1.0
