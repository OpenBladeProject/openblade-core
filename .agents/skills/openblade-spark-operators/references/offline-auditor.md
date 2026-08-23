# Offline auditor handoff

Use `openblade_offline_auditor` for read-only evidence reduction. Give it an exact audit question, repository SHA, and the specific sanitized artifacts or paths it may inspect.

## Appropriate scopes

- fixture hashes, admission-manifest relationships, known-answer reports, and existing evidence-test coverage;
- cached diagnostic archives and existing FirmwareSmoke JSON or Markdown reports;
- localization key, placeholder, culture, packaging, and `qps-ploc` invariants across the four authoritative resource surfaces;
- CI-impact routing, build-script contracts, public API baselines, PR metadata, or documentation consistency;
- call-path or test-gap inventories that do not require deciding the implementation.

## Hard boundary

The auditor must not run builds or tests, write files, inspect unsanitized captures, open HID or installed-service connections, invoke `OpenBlade.Capture` or FirmwareSmoke, start USBPcap or Synapse, or decide that evidence authorizes production support. When execution would help, it returns the exact proposed validation command for the primary agent to route separately.

## Required report

Return confirmed observations with file paths, unresolved questions, the smallest recommended validation commands, and a clear separation between mechanical inconsistency and product or safety judgment. Do not propose a source change unless the primary agent explicitly asks for options after reviewing the evidence.
