---
title: Recognize rznb11 hash mismatches as unreviewed payload evidence
author: Jack
date: 2026-10-20
branch: feat/thx-driver-provisioning
---

## Decided

A reporter's rznb11 APO4 screenshot shows a different payload set from OpenBlade's reviewed 4.2.29.1159 package. The official original APO DLL is 6,229,424 bytes (Explorer 6,084 KB); the screenshot shows 13,390 KB. The original catalog is 182,193 bytes (178 KB), versus 240 KB shown; gRPC is 13,073,504 bytes (12,768 KB), versus 12,880 KB shown. Rounded matching INF/data sizes do not identify a version or establish matching bytes.

The shipped nightly's incompatible-or-unreadable package error occurs after native-client verification. This specific branch has one readable candidate and no missing expected files; four failed verification while the omitted data.bin passed the expected hash. Presence is therefore distinct from admission. The exact installed build and cause of each failed read/hash remain unknown.

## Tried and ruled out

The earlier support ZIP records only the missing native client and has no APO DriverVer or file hashes. Current official product710 middleware still advertises the reviewed 4.2.29.1159 carrier; available cached 709/710/711 manifests agree. No second cached APO4 payload was available. Screenshot timestamps, rounded sizes and DriverStore folder suffixes cannot establish DriverVer. The separate Windows Update service-only finding does not identify this APO4 package.

## Watch out

Collect the target INF DriverVer plus exact sizes and SHA-256 values with a read-only file command, then verify the original signed source and complete stack pairing before adding a build. Do not infer a legitimate newer build, admit arbitrary hashes or prescribe a downgrade from screenshots. The grouped error still permits access/read failures. Rznb11 is shared by the admitted RZ09-0528 and RZ09-0530 profiles; any verified support change should consider both. The legacy collector targets different laptops and must not be used for this report. No compatibility pin or runtime behavior was changed by this finding.
