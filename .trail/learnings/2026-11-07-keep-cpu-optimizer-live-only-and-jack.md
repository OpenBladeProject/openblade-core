---
title: Keep CPU optimizer live-only and preserve coordinated recovery evidence
author: Jack
date: 2026-11-07
branch: feat/cpu-voltage-completion
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - OpenBlade.slnx
  - build/Publish.ps1
  - build/development-deployment/Invoke-LocalDevDeployment.ps1
  - build/development-deployment/Publish-CpuVoltageValidation.ps1
  - build/tools/OpenBlade.LocalDevDeployment.Tool/LocalDevDeploymentReceipt.cs
  - build/tools/OpenBlade.LocalDevDeployment.Tool/LocalDevDeploymentToolApplication.cs
  - build/tools/OpenBlade.LocalDevDeployment.Tool/LocalDevDeploymentToolCommandParser.cs
---

## Decided

RZ09-0528 CPU Voltage Optimizer is live-only. Reject new FirmwareNextBoot Set requests before backend access and independently at the controller; retain serialized targets, observation and exact-original legacy journal recovery. Firmware availability is ObservedOnly. Native full-vector readback and unchanged power peers remain mandatory.

Integrating #623/#699/#700 must preserve the latest #623 readback-reuse, final proof and post-boot recovery changes; the tooling branches forked older product code. Current main uses service IPC72, so the coordinated CPU integration uses73/read-only51.

## Tried and ruled out

Do not use the product710 Synapse getter callback as passive measurement: the pinned policy converts zero to minus1 and calls its setter. BIOS2.02 AodDxe also discards mirrored saved-write status; legacy recovery still requires complete independent paired readback. EC1.09 provenance alone establishes no optimizer command.

## Watch out

Archive CPU installer logs before launch or rollback cleanup. Check the durable in-flight latch before acquiring a closed CPU lease because that acquisition can delete old staging. Known never-launched rollback needs exact Prepared/no-process evidence, a confirmed failed installer, exclusive host lease and independent original-payload proof. None of this resolves the original laptop's unknown CPU owner cleanup, and source tests do not grant hardware authority.
