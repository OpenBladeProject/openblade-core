---
title: Bind inbox audio to the verified Windows signer
author: Jack
date: 2026-10-19
branch: feat/thx-driver-provisioning
touches:
  - src/OpenBlade.SessionAgent/WindowsInboxAudioTrust.cs
  - src/OpenBlade.Tray.Core/ThxNativeComponentTrust.cs
  - tests/OpenBlade.Tray.Tests/ThxNativeComponentTrustTests.cs
---

## Decided
Windows inbox audio trust must bind the required Microsoft Windows publisher to the signer returned by successful WinVerifyTrust provider state. Reuse the existing THX verified-signer extraction and retain the file handle/provider state until verification ends. The separate publisher-only entry always uses cached revocation and requires an exact subject; it permits Windows certificate rotation without inventing a certificate or OS build pin.

## Tried and ruled out
Do not establish signer identity with X509Certificate.CreateFromSignedFile followed by an independent generic signature check. Those operations can refer to different certificates. Catalog membership is still required after verified-publisher trust. Existing THX client entry points continue to require both reviewed subject and certificate thumbprint; null or empty client pins cannot select the publisher-only policy.

## Watch out
Focused tests cover wrong publishers, rotation, missing client pins, cancellation, unsigned files and retained caller handles. Final read-only capture on the exact RZ09-0581 passes with verified publisher and actual inbox membership. This confirms current-host admission only; no clean install, restart, audio mutation or vendor execution was performed. Keep packaging evidence tied to its actual source revision when later trust-only code changes are validated separately.
