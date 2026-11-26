---
title: How OpenBlade app updates are checked and installed
author: Jack
date: 2026-11-26
branch: claude/openblade-update-install-ebda6e
cost: { minutes: 1, tokens: 90456 }
---

## Decided
OpenBlade app updates are notify-only. UpdateCheckViewModel polls GitHub Releases (Stable/Nightly, ETag, rate-limit backoff, interval schedule with first-run consent) and yields only version, tag and release-page URL. The tray shows a toast once per tag and a "View release" link; no installer asset is downloaded or launched. The user runs OpenBlade-Setup.exe manually, and the bundle calls OpenBlade.Setup install (SetupCommandRunner.RunAsync), which stages the payload, stops service/tray/agent, activates it transactionally, and reinstalls and verifies the service.

## Tried and ruled out
Searching for DownloadUpdate/InstallUpdate in the tray finds nothing. The "WindowsUpdate"/"Download" hits are the SnapTap Razer keyboard driver installer (RazerKeyboardDriverInstaller.WindowsUpdate.cs), not app self-update.

## Watch out
RazerFirmwareUpdateChecking is a separate notify-only laptop firmware check. A self-updater would need to respect the bundle-managed ownership check in Setup and launch the bundle at normal integrity (not elevated), so the tray and Session Agent are not inherited as elevated.
