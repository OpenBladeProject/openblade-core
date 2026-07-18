# Changelog

All notable changes to OpenBlade are documented in this file. The project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- A lightweight per-user SessionAgent that exclusively owns Windows display
  queries and changes while the LocalSystem service remains the hardware and
  profile authority.
- Built-in display controls for driver-enumerated fixed refresh rates, EDID-reported Windows Dynamic Refresh Rate, HDR, and the locally installed Razer color profiles.
- Keyboard lighting controls under validation for Off, Static, Spectrum Cycling, single/dual/random Breathing, Reactive Short/Medium/Long, color, and brightness on the exact RZ09-0581/PID 02E0 device.
- Data-minimized Reactive input through Windows Raw Input: scan-code make events map directly to matrix slots without recording characters, virtual-key values, applications, window titles, or input packets.
- Sanitized Synapse oracle USBPcap evidence for lighting brightness readback, the 102-slot matrix envelope, effect timing, dual/random Breathing, and Reactive key placement and decay. This capture evidence is distinct from pending installed OpenBlade visual validation.

### Changed

- Display profile automation now remains active after Tray Exit. The first
  confirmed source is observation-only; later real AC, USB-C, or battery
  transitions apply the destination profile through one serialized agent queue.
- The SessionAgent starts independently at sign-in, while the existing
  Start-at-sign-in setting continues to control only the visible tray.
- Replaced the page-level legacy `ScrollViewer` controls with InteractionTracker-based `ScrollView` controls for smoother high-refresh-rate scrolling.
- Display polling and mutation readback now run Windows topology, registry, and ICC work off the UI thread; unchanged option collections are reused and background checks no longer disable controls, removing a confirmed periodic high-refresh jank path.
- A transient adapter recheck that retains the last confirmed power source no longer flashes the dashboard as `Controls unavailable`; hardware writes remain blocked until classification recovers.
- Refresh-rate choices now match the modes Windows exposes for the internal panel: 60 Hz, 240 Hz, and Dynamic 48-240 Hz.
- Reactive decay now matches captured Synapse timing: 0.5 seconds Short, 1.5 seconds Medium, and 2.0 seconds Long.
- Named-pipe protocol version 9 reports explicit host-renderer ownership in addition to the typed, bounded Reactive key-pulse operation introduced in version 8.
- Performance, fan, lighting, and built-in display choices now form one profile per power source. Changes apply immediately to the active profile, remain a compact unsaved draft until saved, and are discarded on a power-source transition.
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
- Lighting writes remain fail-closed unless the exact model, PID, BIOS, typed effect parameters, pinned oracle evidence, acknowledged transition handshake, and bounded 102-slot matrix report match. Full visual parity is not claimed until installed OpenBlade validation succeeds.
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
- Reactive oracle captures now cover alphanumeric anchors, punctuation, modifiers, arrows, F1-F12, Delete, Fn, and the Fn+F12 Print Screen chord. Interior alphanumeric positions derived from the matrix layout and Windows Raw Input delivery, especially Fn, still require installed OpenBlade visual validation. The power button was deliberately excluded and no numpad is present on this keyboard.
- Fan control provides Automatic and validated Maximum modes, not custom fan curves.
- Unrecognized external adapters remain read-only.
- Unsupported or unverified controls remain unavailable rather than attempting arbitrary firmware commands.

[Unreleased]: https://github.com/boyuanx/openblade-core/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/boyuanx/openblade-core/releases/tag/v0.1.0
