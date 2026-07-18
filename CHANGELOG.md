# Changelog

All notable changes to OpenBlade are documented in this file. The project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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

- Renamed the project from OpenSynapse to OpenBlade to reflect its Razer Blade-only scope.
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
- Lighting remains unavailable because the current firmware exposes no independently verified state readback.
- Fan control provides Automatic and validated Maximum modes, not custom fan curves.
- Unrecognized external adapters remain read-only.
- Unsupported or unverified controls remain unavailable rather than attempting arbitrary firmware commands.

[Unreleased]: https://github.com/boyuanx/openblade-core/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/boyuanx/openblade-core/releases/tag/v0.1.0
