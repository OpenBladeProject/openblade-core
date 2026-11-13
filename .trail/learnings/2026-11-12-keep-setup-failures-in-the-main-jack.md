---
title: Keep Setup failures in the main installer log
author: Jack
date: 2026-11-12
branch: fix/installer-failure-details
touches:
  - CHANGELOG.md
  - build/installer/OpenBlade.Installer.UI.Tests/InstallerProgressPipeTests.cs
  - build/installer/OpenBlade.Installer.UI/InstallerProgressPipe.cs
  - build/installer/OpenBlade.Installer.UI/OpenBladeBootstrapperApplication.cs
  - tests/Build.Tests/InstallerAuthoring.Tests.ps1
  - build/installer/OpenBlade.Installer.UI.Tests/InstallerPackageLogTests.cs
  - build/installer/OpenBlade.Installer.UI/InstallerPackageLog.cs
---

## Decided

Copy the native Setup package diagnostics into the main Burn log on the engine package-completion callback, before rollback or cache cleanup. Import a bounded recent snapshot on failure and subsequent rollback; keep normal success concise. Include exception trees and stack locations, and format progress-pipe failures readably so support does not require a second log file.

Admit the package log through the same opened Windows file handle before reading: require a regular disk file, one link and the expected final path. Keep byte, record and character bounds; mark unavailable or truncated data. Strip BOM and incomplete leading UTF-8 bytes without discarding a partial exception record.

## Tried and ruled out

Raw Base64 progress events and a separate package log left the reporter's main log without the underlying exception. Logging on the pipe reader would reintroduce a completion-callback dependency. Dropping everything through the next newline after a tail seek could discard a large final exception. Persistent offsets are unnecessary: fresh bounded tails tolerate replaced/truncated logs and retain the newest rollback records.

## Watch out

Import is independent of interactive/passive/quiet UI mode. Diagnostic read or sink failure cannot replace the installer result. Keep full imports on failure/recovery, sanitize decoded controls and Unicode separators, and preserve the original failure before rollback appends. This adds diagnostics only and grants no installation or hardware authority.

The existing Trail gate reports lexical overlaps with unrelated model admission, sampling and UI rules; retain the checker and explain the scope rather than changing those policies.
