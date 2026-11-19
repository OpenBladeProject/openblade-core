---
title: Normalize documented PawnIO service path aliases before admission
author: Codex
date: 2026-11-15
branch: fix/pawnio-install-diagnostics
touches:
  - src/OpenBlade.Platform/PawnIoRuntimeAdmission.cs
  - tests/OpenBlade.Platform.Tests/PawnIoRuntimeAdmissionTests.cs
  - CHANGELOG.md
---

## Decided
The logging-only PR975 did not fix installer path rejection. A new primary-source review establishes a real normalization defect: Microsoft Windows-driver-samples at commit7895dd22785ddba5e973662ed942be3b3452b89d, audio/tests/PinResourceHelpers/PropertyHelper.cpp, passes QueryServiceConfigW.lpBinaryPathName to GetFullPathFromImagePath (571-605); its resolver recognizes kernel DosDevices and question-mark prefixes and both SystemRoot spellings (635-693). Source: https://github.com/microsoft/Windows-driver-samples/blob/7895dd22785ddba5e973662ed942be3b3452b89d/audio/tests/PinResourceHelpers/PropertyHelper.cpp .
Normalize only the additional literal percent-SystemRoot prefix using the Windows-provided directory, and the kernel DosDevices prefix. Keep canonical path, direct signed DriverStore package, PawnIO filename, catalog trust, present device, binding and service admission unchanged. Do not expand arbitrary process environment variables.
The old implementation failed four regression cases and rejected both documented aliases of this machine's independently admitted PawnIO2.2.0 DriverStore file. After the fix both aliases resolve to the identical path and driver SHA256, while all209Platform tests pass, including15new rejection cases. This is a source-backed parser defect and a read-only installed-file reproduction, not an end-to-end alternate service registration or proof of the reporter's missing binding.
## Tried and ruled out
A fabricated bad System32 drivers path reproduces an error message but does not demonstrate a defect. The original OBL3 log omitted the registered ImagePath, so do not claim exact reporter attribution or recovery. Do not reinstall or mutate a shared service to manufacture that state. Registry spelling can differ from QueryServiceConfigW spelling: local ACPI's raw relative path becomes a SystemRoot-prefixed SCM path. No speculative quoted, extended-user-namespace, short-name, generic-environment or Windows-relative parser expansion is needed for this fix.
The earlier logging learning was saved incompletely by Windows cmd multiline --body argument handling. Keep that immutable record; this complete stdin-based note supersedes the logging-only scope and records the functional fix. Pipe multiline learning text into trail note on Windows.
## Watch out
Source review of PawnIO2.1/2.2 INF establishes DIRID13, not permission to admit an unsigned, unrelated or legacy System32 drivers binding. All new aliases must resolve to the same already-required DriverStore location and then pass existing independent trust checks. Keep regression assertions about outcomes and refusal, not just normalization branch coverage. Shared Platform fix benefits setup, journal reconciliation, post-install readback and CPU helper on every model; no new hardware commands or model gates are added.
Trail check vocabulary matches on historical version digits, source metadata, driver events, presentation fields, idle polls or uncertain writes do not describe this bounded string parser and logging change. No additional native calls, service changes, installer replay or device I/O are introduced.
