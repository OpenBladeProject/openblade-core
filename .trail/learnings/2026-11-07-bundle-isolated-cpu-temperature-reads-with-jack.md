---
title: Bundle isolated CPU temperature reads with verified shared PawnIO setup
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - AGENTS.md
  - CHANGELOG.md
  - OpenBlade.slnx
  - build/Publish.ps1
  - build/PublishPayloadValidation.ps1
  - build/installer/Bundle.wxs
  - build/installer/OpenBlade.Installer.UI.Tests/InstallerWindowTests.cs
  - build/installer/OpenBlade.Installer.UI/InstallerWindow.xaml.cs
  - build/installer/OpenBlade.Installer.UI/OpenBladeBootstrapperApplication.cs
  - build/installer/OpenBlade.Installer.UI/Properties/Resources.qps-ploc.resx
---

## Decided
Use the user-approved LibreHardwareMonitor plus official PawnIO path for CPU temperatures shared by telemetry, fan curves and Stop. Pin LHM b4718a6208ebbcf028cc29f2aea30e58b138e830: stable0.9.6 does not recognize Panther Lake. Keep a CPU-only MPL source subset and signed IntelMSR/AMDFamily17 modules with corresponding source and notices. Fail IOCTL and short replies explicitly, clear every sensor before acquisition, reject unknown calibration and select actual package/core or corrected AMD die temperatures.

Keep native acquisition in a separate job-owned helper. Assign the job before the first request, bound each request including startup to one second, and fence replacements until predecessor exit. Missing CPU data preserves existing Stop/curve fail-safes; no new fan command/model admission follows from a shared CPU reader.

The explicit bundling request supersedes the former blanket no-new-driver rule only for CPU sensing. Setup invokes the pinned unchanged official PawnIO installer from protected staging before replacing the product; compatible trusted installations are reused, stopped bindings/reboot results are reported, and shared PawnIO survives uninstall. A protected intent journal survives product purge and prevents uncertain vendor-install replay.

## Tried and ruled out
Do not bundle Razer's CPUID SDK merely because Synapse installed it: its proprietary license and undocumented current integration do not provide OpenBlade redistribution/API rights. Keep the successful IPF diagnostic as exact-host evidence rather than assuming all Intel/AMD systems have that OEM stack.

Unmodified LHM could convert failed transport into zero-filled native output and retain stale aggregate readings. The CPU-only source patch avoids those paths and omits RyzenSMU/CCD, clock, power, GPU and motherboard discovery. Package MSR0x1B1 bit31 is reserved; only core0x19C uses it as ReadingValid.

## Watch out
A passing helper protocol or prior IPF/EC experiment does not physically validate this CPU backend or installed Stop session. Development-time installs and fan tests remain separately scoped. Trail's broad keyword conflicts with old Stop trials do not authorize new writes: this change adds a CPU provider and packaging only, preserving exact fan admission and direct Auto cleanup. Source and module pins are redistribution provenance, not a runtime compatible-driver build allowlist.
