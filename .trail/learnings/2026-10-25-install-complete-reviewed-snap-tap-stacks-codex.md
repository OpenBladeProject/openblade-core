---
title: Install complete reviewed SnapTap stacks with exact model recipes
author: Codex
date: 2026-10-25
branch: feat/snaptap-driver-install
touches:
  - src/OpenBlade.Tray.Core/RazerKeyboardDriverRecipes.cs
  - src/OpenBlade.Tray.Core/WindowsRazerDriverInstallPlatform.cs
  - src/OpenBlade.SessionAgent/RazerSnapTapDriverAdmission.cs
---

## Decided

Extend the existing explicit elevated setup action to separately pinned RZ09-0528/02C6 inputs. The complete recipe includes DKM, virtual consumer, keyboard, mouse and RzCommon packages. The mouse INF creates the control child; the keyboard INF alone cannot supply it. Select the recipe from both exact host identities and bind acquisition, every operation, durable intent and final readback to that recipe. This supersedes the previous 02E0-only installation scope; the other five runtime profiles still lack complete reviewed installation recipes.

## Tried and ruled out

Do not convert generic untrusted packages into setup authority. Known common EndpointMissing and model-filter ProviderMismatch against inbox keyboard.inf only qualify for a separate positive installer inspection. Preserve the original failure and refused-inspection outcome. Unknown images, service bindings, filters, catalog failures and malformed context remain refused. A setter or PnPUtil zero exit is not independent installation readback. The generic RAZER DeviceContol hardware contract can match another connected Razer device, and PnPUtil /install updates any matching node. Read both hardware and compatible IDs before PID filtering; refuse any matching common node outside the target control child, container and MI_02 ancestry. The virtual consumer INF requires mshidkmdf as its upper filter; an unbound child may lack it, but final success must confirm it.

## Watch out

Native read-only verification passed all five 02C6 catalogs and ten INF/SYS memberships with exact signer pins. These checks establish cached artifact trust, not clean installation or recovery. RZ09-0528 setup remains source-backed experimental pending physical validation. The existing public 02C6 asset is anonymously accessible, but its release-record audience metadata still says private/collaborators; catalog PR24 reconciled only 02E0. No vendor bytes are bundled or newly published by this feature. Preserve notices and the fixed artifact pins. A restart or uncertain intent continues to block replay.
