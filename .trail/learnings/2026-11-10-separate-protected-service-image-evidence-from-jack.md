---
title: Separate protected service image evidence from SCM launch evidence
author: Jack
date: 2026-11-10
branch: feat/rust-protected-service-image
touches:
  - tools/openblade-windows-host/src/configuration_writer/protected/service_image.rs
  - tools/openblade-windows-host/src/windows/scm/installed/native.rs
---

## Decided
Verify the fixed Windows known-folder service image through retained, non-reparse NTFS directory and file handles. Require independently authenticated expected build SHA-256 before opening the path and keep the file identity and handle alive together.

## Tried and ruled out
The development fixture ZIP, process image pathname, and SCM registration or PID cannot authenticate the exact native candidate build or identify the generation loaded by SCM. Phase 9f adds no production digest provider, launch admission, or readiness.

## Watch out
The existing ancestor ACL policy allows ordinary users to add subdirectories where Windows needs it. The actual install directory does not grant that exception. A successful file read describes a held file on disk. Actual-VM work must establish a trusted build source, launch-to-staged-generation binding and installed runtime readiness before authorization.
