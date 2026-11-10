---
title: Bind Windows controller evidence to held handles and exact SCM aliases
author: Jack
date: 2026-11-10
branch: feat/windows-rust-controller-observation
touches:
  - rust/crates/openblade-platform-windows/src/controller_presence.rs
  - rust/crates/openblade-platform-windows/src/controller_presence/process_inventory.rs
  - rust/crates/openblade-platform-windows/src/controller_presence/service_inventory.rs
  - rust/crates/openblade-platform-windows/src/process_query.rs
---

## Decided
Phase 5z starts from 93bcaa5c063acdab1c5cc3641dd973b6c667acc8 and extends the inactive controller-process-compile-check feature. WindowsHardwareAccessGuard.cs:64-172,179-222 (fda7e75a2f09cf96327c7b838c4e29794f4d4c6e), ControllerPresence.cs:123-187 (eb823f8c674fbc776e4d57996cbf06a167ddec4e), and RazerControllerServiceAliases.cs:5-34 (0be416bac139e0f6daaade94a3df42648a193605) pin alias selection and positive status classification. The SCM adapter resolves all exact keys and public display aliases, including display aliases under arbitrary keys. Query only resolved keys with query-only access, retain service handles through a second alias pass, and keep positive rows from incomplete scans. Only an explicit not-found alias lookup or a stopped matched Win32 service contributes to absence; paused, stopping, failed and unknown Win32 status warns.

## Tried and ruled out
SCM enumeration can silently omit inaccessible services. CpuServiceInventory.cs:112-135,344-357 (fbae1483502eb02136c19208e906ed730a2f5058) has a separate registry coverage check for its inventory contract. For these fixed aliases, bounded forward and reverse lookups avoid assuming enumeration is exhaustive or adding a registry dependency. A found alias whose status is denied or disappears remains unknown. The GPU consumer keeps its previous access, token SID and exit checks while sharing the low-level process queries.

## Watch out
Toolhelp names and raw parents remain separate from metadata queried on one held process object. Require a live handle, exact PID and executable basename, and a creation FILETIME strictly before the snapshot lower bound; failed, malformed or duplicate evidence precludes complete observation. These observations are point-in-time. An image pathname never becomes VerifiedImage. WindowsControllerProcessParents.cs:83-141 (a76dfeb73e3016f8277e353b40255f7ae41a7a10) and SynapseBladeBlockerVerifier.cs:25-145 (b844586126bea70f7f3a7b4632a6735d039adb88) pin the unresolved helper and patch verification inputs; LampArray acquisition remains unresolved. The adapter is compile-checked and tested with injected rows and errors only.
