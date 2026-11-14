# Unclassified external-power control validation

This evidence supports the generic issue #967 software fix. It does not prove the reported RZ09-0287 charger recovered, identify barrel versus USB-C, or establish physical validation on any model.

## Exact-model source basis

The [immutable source ledger](../2026-11-14-unclassified-external-thermal.json) pins each of the 22 admitted models' own Synapse ordinary setter and adapter helper. Independent verification matched 44 complete-file SHA-256 pins and 66 UTF-8 byte excerpts with zero failures; see [source-verification.json](source-verification.json). A helper failure can leave the vendor ordinary setter reachable through its independent Windows battery-state check. That establishes ordinary command authority, not sufficient wattage or a connector identity.

Existing typed ordinary commands retain exact-device, controller, session, fresh external-presence, source-lock, current-state, applicable metadata and independent readback admission. Correlated read-only adapter terminal rejections can leave the actor usable; malformed replies, transport ambiguity and uncertain writes cannot. Advanced HyperBoost and native tuning remain separately gated. Known limited-power and battery policies are unchanged.

Initial never-classified AC remains Unknown. A previously confirmed power label may be retained through transient classification failure, with RetainedPowerSource explicitly marking that it supplies no current advanced/profile authority. Positive Windows external presence is a separate fact. Explicit intent does not select a saved Full/limited profile or activate another user's dormant override.

## Advisory review

Claude Opus 5.5 high reviewed the initial frozen implementation. Its whole-patch read hit the tool's 256 KiB limit; production changes were read, while source ledger and remaining test coverage were partial. [Initial metadata](claude-initial-metadata.json) records this limitation. Source-byte verification is independent.

Confirmed findings led to completed-observation publication, preservation of non-power/owned-HyperBoost recovery blockers, an ordinary-purpose guard before HyperBoost cleanup, guarded owned manual-fan release, fresh presence in the curve path, and evidence-constant reuse. Follow-up independent review closed retained-source consumers, GPU gating and resume lighting provenance. Unknown-power startup/profile lighting data cannot become restoration authority without captured ownership or an explicit request. Rejected findings would have removed required per-SET admission, guessed a profile/performance destination or treated hypothetical future backend differences as current defects.

The correction delta received a [second immutable review](claude-corrections-metadata.json). Independent triage confirmed power-only block self-latching, stale published presence during curve maintenance, owned Battery Saver Stop release on actual AC, completed-result publication, an out-of-gate generation mutation, a misleading Ready label, shortcut Custom parity, and lighting cleanup ordering. Nine new service regression cases and focused UI assertions cover these corrections. A separate read-only review passed the final correction diff.

Two claims were narrowed: stale presence can prevent curve updates, while Automatic handback is not generally reachable; the public lighting cleanup bypass is real, while the cited lifecycle-only caller does not reach that branch. A future shared presentation-helper refactor and cosmetic duplication were declined. The existing three-argument lighting wrapper remains required by the reflective selector-parity test. No third Claude invocation was made.

## Integration with confirmed external-power modes

Main advanced to the separately reviewed PR #964 while this fix was in progress. The combined implementation preserves its known sufficient/limited mode choice and source-bound HyperBoost recovery while keeping Unknown ordinary admission separate. Final integration passed Protocol 4,972/4,972, Service 4,055/4,055, Contracts 845/845, targeted UI 770/770, Session Agent 1,382/1,382 and native interop 136/136, with no skips. Whitespace verification passed. The native provenance refresh changed the DLL bytes, so all 136 native interop tests were rerun against the exact final DLL. The known-USB-C Custom shortcut was also corrected to use its own profile, with a distinct Full/USB-C tuple regression; Unknown source-free behavior is unchanged.

An owned Battery Saver Stop can outlive an explicit switch to Battery Balanced. Its typed prior-authority release preserves the independently read current 3/6 selector and sends only that selector's captured Auto pair. It does not reset to Balanced, use guardian commands, or make 3/6 selectable on Unknown. The service rechecks scoped ownership and controller absence; the port checks exact 1500/1500 prior-stop shape, source, original preset and staged channel flags before each SET, then independently reads the final same-preset Auto pair. A failure after the first submission remains indeterminate and requires read-only reconciliation; the provider never retries this operation through a fresh session.

The updated source-fingerprint native builder was reused with the canonical user-approved `C:\build-cache\cargo\openblade-core` cache and both existing cache leases, because the shared checkout retains its historical OpenSynapse folder name. No extra target tree or toolchain copy was created. The native linker emitted one informational import-library message; managed baseline builds had zero warnings/errors. Main later incorporated the separately reviewed broker/input-helper fix #966. The final patch is 0.63.2; its full Session Agent 1,382/1,382, deployment client 238/238 and exact native interop 136/136 gates passed again. Local PR-version policy passes against base 0.63.1. No thermal source or command changed in this second integration.

## Disconnected presentation

[Enabled ordinary controls](unclassified-external-laptop.png) shows an isolated RZ09-0300 fixture with ordinary performance, fixed fans and host curves enabled. The Windows power-mode profile association remains deferred. The preview does not connect to the service or hardware.

## Verification limits

The [verification record](verification.json) distinguishes full-suite results, incomplete integration and isolated repeats. The initial full UI run had one proven live language-switch timeout; its unchanged isolated repeat passed. A later serial run passed 14,742 tests across 17 completed projects, then Windows PowerShell aborted on the same UI test's stderr before retaining its failure detail or final suite count. That second failure is not proven to be a timeout. Bounded source review found no blocking operation in the changed presentation paths; the existing synchronous replacement-window prefix is not bounded by its asynchronous phase timeout. No timeout or smoke implementation was changed.

An unchanged malformed-frame pipe fixture failed once and passed its isolated theory and complete 32-test class. Final service/UI results are separately recorded. Hosted exact-head checks are recorded in the PR and remain required before merge.

No hardware write, installation, vendor-code execution, Windows Update action or sleep/resume experiment was performed.
