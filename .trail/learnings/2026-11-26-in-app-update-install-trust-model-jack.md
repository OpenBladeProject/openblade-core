---
title: "In-app update install: trust model, owner pin, and local build limits"
author: Jack
date: 2026-11-26
branch: feat/install-app-updates
cost: { minutes: 30, tokens: 233228 }
touches:
  - AGENTS.md
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.zh-CN.resx
  - src/OpenBlade.Tray.Core/ReleaseUpdateChecking.cs
  - src/OpenBlade.Tray.Core/UpdateCheckViewModel.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.xaml.cs
---

## Decided
In-app update install (feat/install-app-updates): an "Install update" button re-reads /releases/tags/{tag}, downloads OpenBlade-Setup.exe to %LOCALAPPDATA%\OpenBlade\Updates\<version>\, requires the exact size and the SHA-256 from the .sha256 asset (plus GitHub's asset digest when present, which must agree), pins the file with FileShare.Read, refuses if any ancestor folder is a reparse point, and opens the normal graphical bundle. Release installers are NotSigned, so the user accepted hash+size over HTTPS as the trust anchor. The installer is not offered when the tray is elevated (Environment.IsPrivilegedProcess).
The check feed and the installer now use GitHubReleaseUpdateClient.RepositoryApi = OpenBladeProject/openblade-core. The old OSSBlade owner is unregistered and only reached through a GitHub 301, so whoever registers it could serve a "newer" installer with a matching hash.

## Tried and ruled out
Building the full solution on this host: OpenBlade.Core.Native needs rustc, which isn't installed, whatever OpenBladeVersion is set to. Tray/Tray.Core/Tray.Tests don't depend on Protocol/Core.Native, so verify Tray-only changes with the Tray.Tests suite from a subst drive.
A Fable delta re-review of the security fixes was stopped by cyber safeguards; the first full Fable review completed.

## Watch out
Other links still name OSSBlade (UninstallRegistration URLInfoAbout, the WhatsNew changelog link, the issues/repo links in MainWindow.xaml, docs links in ExperimentalBladeModels). They are only links, but they share the same former-owner risk.
Git Bash sed -i strips CR on these Windows files; normalize line endings with PowerShell ReadAllText/WriteAllText instead.
