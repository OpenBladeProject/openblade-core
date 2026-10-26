---
title: Pushed application pages and timestamped telemetry inspection
author: Codex
date: 2026-10-22
branch: codex/profiles-telemetry-ui
touches:
  - src/OpenBlade.Tray/MainWindow.Applications.cs
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/TelemetryChart.cs
  - src/OpenBlade.Tray.Core/DisplaySettingsViewModel.cs
---

## Decided

Application profile editing and installed-game discovery use real pushed WinUI pages. Keep drafts separate from saved state. Lock the native ContentControl form while Save is pending, and use draft/generation identity so an old completion cannot close a newer editor. Discovery publishes only for its current cancellation lifetime and generation. Group by game/install root, remove exact duplicates and support tools, and retain distinct bootstrap/Shipping and rendering variants. Read local executable icons with bounded concurrency and navigation cancellation.

Display brightness uses the shared native slider style and the existing serialized typed adapter. Debounce one completed input interaction; recheck source and availability before committing. Expose an explicit HasBrightnessReading boolean for visibility. WinUI generated x:Bind code null-guards BrightnessPercent.HasValue and can leave an old visible state when the nullable becomes null. The finite slider fallback must remain collapsed when unavailable.

Telemetry uses actual selected time bounds, timestamp/value axes, a vertical crosshair and all metrics from one nearest original raw sample. Drawing downsampling never supplies inspection values or statistics. Preserve gaps and leading/trailing empty time. Defer snapshot construction and chart drawing while hidden without stopping explicit recording.

## Tried and ruled out

A standalone sample slider looked like progress and did not expose timestamps. Snapshot-only imitation editors could not verify the real pushed page. Collapsing an Unreal bootstrap and its Shipping executable loses the path of the actual foreground process. Disabling a StackPanel does not compile in WinUI; use a native ContentControl ancestor so keyboard and pointer editing both lock.

## Watch out

Disconnected captures exercise actual production pages with fixed fixtures, including editor top/bottom, known-to-unknown brightness visibility, native form locking, exact crosshair selection, empty states and known-local executable icon decoding. Fictional game fixtures keep fallback icons. Update translator screenshot inventory and routing with every route, and verify real deterministic package archives in addition to resources and renders.

This refinement changes no hardware commands, model admission, service or Session Agent source, Contracts or IPC. UI-only deployment must preserve installed protected components; the fixed full-install broker is not a tray-only workaround. Trail check currently reports lexical matches for version digits and generic UI terms against unrelated hardware/native-driver learnings; those matches do not authorize or represent changes to those contracts. Review the actual diff and document this scope in the PR.
