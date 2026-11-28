---
title: Share native Service install identity without linking the development host
author: Jack
date: 2026-11-10
branch: fix/native-service-install-identity
touches:
  - tools/openblade-windows-host/src/configuration_writer/protected/service_image.rs
  - tools/openblade-windows-host/src/installed_main.rs
  - tools/openblade-windows-host/src/lib.rs
  - tools/openblade-windows-host/src/windows/scm/installed.rs
  - tools/openblade-windows-host/src/windows/scm/installed/native.rs
  - tools/openblade-windows-host/src/service_install_identity.rs
---

## Decided
The installed native Service and protected image reader compile one shared Windows source module for the fixed install names and Program Files Known Folder lookup. Keep the protected reader in the development library and the Service fail-closed until independent launch and runtime evidence is available.

## Tried and ruled out
Importing the helper from the protected reader links development-host objects into the candidate. Moving the configuration writer into the candidate would widen the boundary for no gain.

## Watch out
Cargo can still compile the package library and show it in aggregate dependency records. Check the candidate's rustc dep-info and the MSVC map for actual isolation. A clean link map does not establish launch authority.
