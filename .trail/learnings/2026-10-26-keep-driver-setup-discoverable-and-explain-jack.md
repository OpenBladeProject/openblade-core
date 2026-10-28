---
title: Keep driver setup discoverable and explain refused inspection before adding Windows Update
author: Jack
date: 2026-10-26
branch: fix/snaptap-foreground-pause
touches:
  - AGENTS.md
---

## Decided
The nightly .103 RZ09-0528 diagnostic proves why the setup option was hidden, but not which installer preflight failed. SnapTap runtime admission saw RzCommon EndpointMissing and the keyboard collection still bound to Microsoft keyboard.inf. Those are known partial states that invoke separate installer inspection. The inspection returned Admitted=false, Installed=false, Unavailable; the UI shows actions only for DriverMissing, so all setup actions disappeared. No installer-specific exception or UnscopedCommonDevice trace appears in the supplied ZIP.

Separate setup action visibility from permission to mutate. An incomplete supported stack should retain a visible setup/help path and a concrete blocked reason. Keep fresh exact-model, topology, controller, trust and operation admission. The setup snapshot/inspection currently drops failed-gate evidence into generic Unavailable; record the concrete failed component and observed state rather than assuming a binary-hash or topology mismatch from this ZIP.

Windows Update is a feasible additional device-targeted source: WUA provides driver-only and optional searches, matched hardware/compatible IDs, download and selected-update installation. Prefer matching reviewed hardware IDs and actual target topology over titles or a broad Razer filter. Revalidate actual coherent installed packages, Windows catalog trust, services and API after installation. This is a proposal, not an implemented or physically validated provisioning path.

## Tried and ruled out
Do not claim that the screenshot predates installer support: the diagnostic records .103. Do not label all installed Razer files corrupt: the known failure is missing binding/endpoint. A compatible independently installed runtime can be accepted while the fixed pinned installer refuses to overwrite different existing bytes. Do not expose installation authority merely by weakening that refusal.

Do not promise that Windows Update always supplies the complete stack or that a successful update return is independent driver admission. Optional offers, PnP/CHID targeting, policy and separately serviced dependencies affect availability. Direct Catalog CAB acquisition already exists for 0255, but it is not a live Windows Update discovery/install backend. Preserve the pinned path as an explicit fallback only when no installation was attempted, not as automatic replay after partial or ambiguous WU installation.

## Watch out
The exact program called WUManager is unconfirmed; David Xanatos WuMgr uses WUA, but do not assign that identity to the owner without evidence. Microsoft documents WUA/orchestrator coexistence concerns and incomplete Settings history for WUA operations (https://learn.microsoft.com/en-us/windows/win32/wua_sdk/portal-client). Resolve coordination explicitly before implementing; never disable Windows Update or change policy as an app shortcut. Search/selection reference: https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search and https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iwindowsdriverupdate-get_driverhardwareid. No WU scan, download, install, UAC or hardware action was run for this investigation.
