---
title: Reject null profiles and serialize confirmed profile saves
author: Jack
date: 2026-11-01
branch: fix/codebase-reliability-audit
touches:
  - Directory.Build.props
  - src/OpenBlade.Capture/ExternallyLaunchedHyperBoostWorkloadSource.cs
  - src/OpenBlade.Capture/StockSteelNomadWorkload.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Protocol/ProductionLightingEvidence.cs
  - src/OpenBlade.Service/PadlessHyperBoostLifecycleValidationBuild.cs
  - src/OpenBlade.Service/ProfileStore.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/ThermalExternalDriftValidationBuild.cs
  - src/OpenBlade.Session/DisplayProfileApplier.cs
---

## Decided
The codebase reliability audit rejects null required power profiles at IPC and profile-store normalization boundaries. Invalid framed requests stay connection-local; corrupt current and legacy profile documents retain their original bytes in quarantine. A committed profile save reports Stored even if its separate lighting restore fails, retaining restore diagnostics.
All seven Tray whole-document profile writers share one persistence gate, including candidate construction and confirmed local update. Reconcile service state before a later save because successful replies carry no status revision and an older buffered watch notification can otherwise regress the local document. A row toggle updates an existing ID rather than upserting a stale copy. Keep rejected or uncertain drafts, and never replay an uncertain save.
Replay requests have source generations independent of enable generations: a newer open or Return to live supersedes older success and failure completions. Application-profile recency has a different identity from its process-exit subscription. Display brightness unavailability preserves any earlier submitted refresh/HDR/color operation and therefore pauses automation.
Executable and assembly identity hashes stream through a 64 KiB sequential buffer. A synthetic 32 MiB file produced identical SHA-256 with approximately 64 KiB allocated instead of 32 MiB; default stream buffering was slower, so do not infer latency improvement from allocation reduction alone.

## Tried and ruled out
The earlier telemetry range/pin/source findings were already fixed on the audited main. Existing GPU-slider startup and Lighting-Off/profile reports remain unreproduced and do not justify speculative hardware changes. File size alone did not establish a reason to split every runtime/UI monolith; extracting the shared persistence boundary addresses an actual ownership defect. Do not bulk-add ConfigureAwait(false) to UI-affine methods or globally enable nullable-deserialization enforcement as a compatibility shortcut.

## Watch out
Once a profile save commits, hardware-restore failure does not mean persistence failed. A post-send failure can still follow durable persistence; read independently before the next edit. Preserve fresh device admission, stop-on-ambiguity and saved intent. The application-lease task captures its cancellation token before awaiting because renewal may dispose the old source. Source tests and disconnected UI coverage do not constitute physical validation. No installed binaries, drivers, firmware settings or sleep state were changed by this audit.
