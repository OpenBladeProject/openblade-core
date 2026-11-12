---
title: Pin embedded map checkout bytes across Windows and Linux
author: Jack
date: 2026-11-12
branch: feat/issue945-lighting-parity
touches:
  - ".gitattributes"
---

## Decided
Give every hash-pinned embedded fixture an explicit Git eol contract. Blade14KeyboardLightingLayout.json's approved9356BC... hash covers CRLF bytes, so declare text eol=crlf beside the existing Blade18 map rule. This guarantees the approved bytes on Linux and Windows without changing the table or pin.

## Tried and ruled out
A clean Windows test run and CRLF working copy do not prove portable fixture bytes: core.autocrlf normalized the tracked blob to LF, and an unspecified checkout on Linux would embed a different hash. Do not repin the resource to incidental local checkout bytes or rely on machine-wide Git settings.

## Watch out
Verify the smudged checkout, not git show's normalized blob. For this map, git cat-file --filters with both core.autocrlf=false/core.eol=lf and core.autocrlf=true must yield SHA2569356bc177dfbcb458e4c9e465d93ad7e32af94a61adbdf7c7e2a7319a7d3e623. Preserve source JSON pins and the declared .editorconfig convention together.
