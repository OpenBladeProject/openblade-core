---
title: Review translated feature flows for natural wording
author: Jack
date: 2026-10-19
branch: feat/thx-component-download
touches:
  - AGENTS.md
  - Directory.Build.props
  - docs/localization.md
  - docs/thx-audio.md
  - src/OpenBlade.Contracts/RazerBladeModelCatalog.cs
  - src/OpenBlade.SessionAgent/ThxAudioAdmission.cs
  - src/OpenBlade.SessionAgent/ThxAudioNativeAdapter.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
---

## Decided
Translate complete feature flows with the English source, behavior and rendered context. Use the reviewed THX concept glossary in docs/localization.md, keep visible labels aligned with instructions and accessibility names, and give changed wording a separate language review. Review Chinese and Russian together when shared audio terminology changes.

## Tried and ruled out
Literal English terminology produced awkward Chinese audio labels and Russian saved-sound phrases. Resource key/placeholder validation and pseudo-localization passed those translations because neither checks naturalness. Do not add string-equality tests that merely freeze the revised wording and claim they prevent this class of problem.

## Watch out
Use an independent wording pass and prefer native-speaker input when available. Record uncertain terms rather than claiming mechanical checks prove language quality. Re-render changed screens for wrapping and context. Preserve exact compatibility claims, named buttons, recovery instructions and protocol-neutral resources while improving phrasing.
