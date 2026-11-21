---
title: compare confirmed maximum fans in custom matches
author: Jack
date: 2026-11-20
branch: feat/rz09-0482-maximum-fans
---

## Decided

ControlCoordinator.PerformancePresetMatches compares the confirmed Custom MaximumFans flag whenever the capability admits it, in the scoped, full-transition and GPU-offsets-only paths. Before this, a flag-only change returned Unchanged with no write on 029F, 029D and 02A0, and the HyperBoost exit restore reported success with the flag still cleared. Cpu, Gpu and the flag stay compared one by one; 5f4d4280 narrowed from record equality so that the legacy GpuClockOffsets field could not cause false mismatches.

The RZ09-0482 07/0F writes use the status-only contract (variable response, payload of 1 byte or less, no echo). The pinned 669 setter V (rzDevice25 byte 52963) and transport (bytes 119015/119262) never compare the payload. Other 029D setters keep the echoed acknowledgement.

A readback-confirmed HyperBoost Maximum Fans cleanup resets `submitted` before mode 7, so a later admission refusal is KnownNotApplied, not Indeterminate.

CPU Boost (bit 0x01, 669 main byte 162583) is recorded as PowerModeCpuBoost SynapseSourceObservedOnly, with no control.

## Tried and ruled out

A disable-only battery Maximum Fans exception for 029D/029F, like 02B6/02B7/02B8, has no caller. These models admit no battery thermal preset: RequirePerformanceWriteAdmission and the Custom thermal admission both refuse on battery before any write. Synapse 669 clears the flag before its DC Balanced 6 setter, and admitting that would be a separate preset review.

Reporting Maximum Fans as unknown after a failed 07/8F read is not possible: CustomPerformanceSettings.MaximumFans is a bool, and reporting false would be a guess. The Custom query fails closed instead, as it already did on 029F/02A0.

## Watch out

Service tests need a fake device that clears the flag on HyperBoost entry (ClearsMaximumFansForHyperBoost) to see the restore bug. The default fake leaves CustomPerformance untouched, so the old matcher looked correct. Physical behaviour is still unvalidated on every model.
