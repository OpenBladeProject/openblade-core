---
title: Count present PawnIO devices separately from disconnected registry instances
author: Jack
date: 2026-11-14
branch: fix/issue969-pawnio-admission
touches:
  - Directory.Build.props
  - src/OpenBlade.Platform/PawnIoRuntimeAdmission.cs
  - tests/OpenBlade.Platform.Tests/PawnIoRuntimeAdmissionTests.cs
---

## Decided
For issue #969, count only PawnIO instances that CM_Locate_DevNodeW locates with CM_LOCATE_DEVNODE_NORMAL. Enum registry keys can retain disconnected instances after uninstall/reinstall; their raw count is not the present-device count. Skip only CR_NO_SUCH_DEVNODE (0x0D). Require exactly one present instance, retain selected Service=PawnIO and Root\PawnIO hardware ID checks, signed DriverStore package validation, service-state semantics and installer journal replay protection. Include present/registered counts on true ambiguity. This shared Platform fix applies to Setup, journal reconciliation, post-install readback and CPU-helper runtime admission on every supported laptop. Official PawnIO 2.2.0 INF: https://github.com/namazso/PawnIO/blob/2.2.0/PawnIO/PawnIO.inf.in. Microsoft NORMAL-versus-PHANTOM contract: https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_locate_devnodew.

## Tried and ruled out
Do not accept the first present instance, ignore arbitrary ConfigManager errors, count phantom registrations as live devices, remove shared registrations, or retry an uncertain installer. Trail check's lexical conflicts around version digits, device-node opens, InvalidDataException, query failures and cancellation do not describe this patch: the required SemVer bump is 0.61.2 to 0.61.3; CM_Locate is a read-only presence query, not a driver open; native failures and cancellation still reject; no exception downgrade or unknown driver/model admission is added.

## Watch out
The reporter's screenshot identifies the failing cardinality check but does not distinguish zero live nodes from disconnected duplicates. This patch fixes a demonstrated source defect and improves diagnostics; reporter-side installation recovery still needs their logs or a retest. Zero/multiple present nodes and incomplete or untrusted shared installations remain failures. No development-time driver installation or hardware write is authorized by the source/software fix.
