---
title: Delegate sustained Stop through a surviving owner and preserve lower-power intent
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - OpenBlade.slnx
  - build/Publish.ps1
  - build/PublishPayloadValidation.ps1
  - docs/so6120-fan-stop-crash-recovery.md
  - docs/so6120-guardian-caller-loss-validation.md
  - src/OpenBlade.Capture/DirectDutyAutomaticExit.cs
  - src/OpenBlade.Capture/OpenBlade.Capture.csproj
  - src/OpenBlade.Capture/So6120DirectDutyCoordinator.cs
  - src/OpenBlade.Capture/So6120DirectDutyJournal.cs
---

## Decided
Integrate the typed Stop/Auto switch through an exclusive service hardware lease and a separate surviving SO6120 owner. Retain the provider semaphore until same-operation Auto, owned cleanup, journal completion, native release and actual owner exit are all confirmed. Stop is activation-local and sustained until explicit Auto, an explicit mode change or failed continuation safety. External power upgrades request Auto; lower-power transitions retain Stop only after verifying the existing owned controls, without replaying zero or FE. Preserve destination profile intent until release. The service delegates the existing typed adapter read because Windows external-power metadata alone cannot distinguish USB-C and the full adapter.

## Tried and ruled out
Do not put the long-lived service in a new kill-on-close job per activation. A hardware-free proxy owns that job; the native owner escapes it. Matching raw Auto, process exit alone, a completed old journal, or a missing marker must never adopt another operation. Do not send legacy zero-RPM commands or use Balanced/half-cycle as an invented transition prerequisite. The prior attended caller-loss result is evidence for its frozen Capture path, not physical validation of the new installed Session-0 path or sustained Stop. The branch-wide Trail check's twenty lexical overlaps with older fan/RPM notes do not describe these typed direct-PWM commands and their later evidence.

## Watch out
The surviving owner still cannot guarantee cooling after its own termination, native-call hangs, unknown outer-job termination or OS failure. Same-power external firmware mode changes do not yet have an established continuous notification contract; the ordinary thermal query may reject FE. Keep this limitation explicit. Initial service entry remains exact-model USB-C Balanced, with the already installed pinned driver and fresh native admission. A lower-power policy is not authority for another model or firmware. Installation, rollback, removal and purge must refuse to disturb active guardians or pending ownership evidence. UI cancellation must not cancel mandatory confirmation, and a known-unsubmitted keyboard attempt blocked by guardian delegation may be retried only after verified handback.
