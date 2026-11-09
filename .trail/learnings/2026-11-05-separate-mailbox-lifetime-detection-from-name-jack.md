---
title: Separate mailbox lifetime detection from name visibility and ACPI root coverage
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - tools/mailbox-observation
  - docs/so6120-mailbox-calibration.md
  - docs/so6120-acpi-event-coverage.md
---

## Decided

Use the first calibration to size capture and export together: recorder v2 caps Object/ACPI at256/32MiB, preserving32+32/4+4MiB headroom. Export and inspection bound256MiB ETL,4M records,1.5GiB JSONL; export deadline300s. Version1 manifests retain their original128/16MiB contracts.

The separately authorized v2 calibration prepared the file before tracing and successfully queried/read the reopened handle. ETW still omitted its transient name. The unique0.8046ms lifetime is correlated inside the narrow QPC/UTC envelope; a different File object reused the same PID/handle0.605ms later, and the original object pointer was reused later too. Do not borrow those names. Keep the strict named-control failure and unnamed populations explicit. A source-reviewed one-open/close target-device positive control without I/O requests is more relevant than another filesystem variant, and needs its own approved owned-session procedure.

## Tried and ruled out

The ACPI Event7 descriptor's decoded references resolve to context start120D0 and cleanupAF70. Ordinary/EX external new contexts and root EC query evaluation reach them; nested AML frame setup and synchronous context reuse bypass a separate event. The provider check has no additional Event7 name/duration filter, but emission errors are not checked. Negative traces therefore cover observed new-context roots conditionally, not every nested method. Repeating quiet traces cannot repair that static blind spot.

## Watch out

Both live calibrations stopped their own sessions with zero reported loss and confirmed absence. Neither ran a physical-action window, opened a driver device, issued port/AML commands or changed fan/profile state. V2 marker preparation did not establish named transient coverage. Partial visibility can inform a finite residual-risk decision; it neither proves a competitor nor supplies hardware ownership. Preserve raw traces privately. Source-backed direct-control implementation and actual hardware admission remain separate work.
