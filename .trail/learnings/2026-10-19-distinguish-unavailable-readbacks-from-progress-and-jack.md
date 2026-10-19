---
title: Distinguish unavailable readbacks from progress and identify admission blockers
author: Jack
date: 2026-10-19
branch: fix/739-offline-controls
touches:
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray.Core/MainViewModel.Status.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
---

## Decided
A null one-shot firmware readback means unconfirmed state, not work in progress. Show progress only during the typed transient observation reasons or an explicit apply operation. After a completed unconfirmed read, show the existing localized Unavailable label and keep that control's edit and command paths disabled. Retain prior observations only as presentation history; fresh confirmed readback re-enables each control independently. Host-managed Gaming Mode retains its existing saved-policy behavior.

The status metadata fast paths must compare ObservationReason. A refresh-only transition otherwise changes the computed loading getter without sending PropertyChanged to XAML. Test the actual binding notification on transitions in both directions, not only the getter value.

Experimental write-admission diagnostics must identify the failed guard and record it in the existing bounded observation history. Keep the same short-circuit order, fresh checks, KnownNotApplied result and zero transport traffic on rejection. Do not infer a specific controller or session cause from an opaque additional-admission callback.

## Tried and ruled out
The October 19 owner reports are on nightly.95 and contain zero protocol or input observations. They do not justify changing Maximum Fans bit semantics, Fn topology admission, or temperature mappings. Reuse pinned vendor definitions and the existing reviewed commands. The owner tried Boost/High and Low/Low separately; the report does not say one pair changed into the other. Factory-charger use is established for these checks.

## Watch out
The fan-curve restoration remains unconfirmed. Diagnostic and presentation fixes do not establish physical recovery. Keep issue739 open for those unresolved symptoms and do not prescribe another broad owner test round. Reuse existing On, Off, Checking and Unavailable translations. Disconnected preview states exercise the real controls on private desktops without service, Session Agent or device access; animated progress rings cannot satisfy the stable-frame snapshot comparison, so verify their unchanged in-progress behavior with focused binding tests.
