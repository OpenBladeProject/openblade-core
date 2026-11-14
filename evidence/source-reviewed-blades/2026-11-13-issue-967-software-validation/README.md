# Issue 967 software validation

This is software and source-review evidence. It does not establish physical fan response, restoration, lifecycle behavior, or recovery of the reported RZ09-0287.

## Review

Claude Opus 5.5 at high effort reviewed the frozen working tree. The first advisory run did not read the supplied patch, so its coverage was treated as incomplete. Its confirmed findings led to fixes for protocol 51 placeholders, fresh curve projection, saved-intent preservation, editor bounds, profile admission, exchange duplication and range diagnostics. Profile-bank fallback and hypothetical future mapping refactors were rejected against the pinned source and current paths.

The corrected run read the exact supplied patch. Its compact diagnostics are in [claude-metadata.json](claude-metadata.json). The source ledgers were independently checked against downloaded bytes; Claude's review does not substitute for that verification.

Confirmed follow-ups:

- Automatic can retain another reviewed preset's targets. Admit the exact independently read retained tuple against the exact model's reviewed range union, then check every new target against fresh current-preset limits and confirm each staged pair by exact equality.
- Reject Maximum on models without that explicitly admitted mode. Preserve the established RZ09-0528 Maximum contract and its legacy stored alias without changing saved intent.
- Remove the duplicate RZ09-0301 fan-envelope evidence key.
- Explicit manual entry submits targets even when firmware already reports the requested pair, so matching state cannot establish service ownership. Typed maintenance retains no-op completion only behind the existing ownership, epoch and desired-revision admission. The separately validated production backend already submits both targets.

Other findings were checked and declined:

- The profile 1 GET bank on RZ09-0328/RZ09-0530 follows their pinned current Synapse definitions. A speculative profile 0 fallback is not admitted. Physical firmware-variant behavior remains untested.
- The provider's second-zone continuation phase is produced only by the separately validated ProductionBatteryPort. Those models have one global range; the new preset-ranged experimental backends cannot enter that continuation phase. Its existing maximum remains appropriate for that scope.
- Fresh no-op curve reads while firmware clips a target are intentional: ordinary thermal polls do not refresh range metadata. Comparing original intent with actual targets discovers a later range increase without a redundant SET. Cached limits never authorize writes.
- Static allocation cleanup and possible future preset-map drift do not establish current correctness failures. Current mappings and all required per-SET admission remain covered.

Only test-fixture corrections, the disconnected preview's removal of an inaccurate limited-power flag, and the listed verified follow-ups changed after the corrected review snapshot. An independent Codex review checked the follow-ups.

## Rendered controls

[Creator curve at 5,500 RPM](rz09-0300-creator-5500.png) shows a disconnected RZ09-0300 fixture with the 3,900–5,500 RPM reviewed Creator envelope. Runtime assertions require both curve endpoints and editor bounds to retain 5,500. [Manual fan choices](manual-fan-controls.png) records the shared controls. Neither renderer connects to the service or hardware.

## Verification

Final integrated results are recorded in verification.json. Targeted regressions cover narrowed and widened metadata, unchanged saved profiles, actual target caching, staged range changes, source changes, rejected/ambiguous submissions, legacy compatibility and editor bindings. No hardware write, installation, updater execution or sleep test was performed.
