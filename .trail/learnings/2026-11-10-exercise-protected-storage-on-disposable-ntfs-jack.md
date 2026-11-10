---
title: Exercise protected storage on disposable NTFS
author: Jack
date: 2026-11-10
branch: test/rust-ntfs-admission
touches:
  - ".github/workflows/rust.yml"
  - tools/openblade-windows-host/src/configuration_writer/protected/tests.rs
  - tools/openblade-windows-host/src/configuration_writer/protected/tests/hosted.rs
---

## Decided

Phase 4i compares the managed Setup #912 descriptor with the Rust admission policy and gives the live NTFS round trip its own hosted Windows check. The fixture opens the fixed drive first, creates one private child atomically, and keeps the admitted root handle through a transaction that changes one logical profile and its commit envelope.

## Tried and ruled out

A synthetic descriptor or the regular user-temp fixture cannot establish positive Administrator admission on NTFS. The full Rust matrix includes an SCM smoke and is outside this focused run. The negative hard-link case needs no ACL relaxation.

## Watch out

The fixture ancestor is the hosted system-drive root. It does not establish the installed machine-data ancestor layout, LocalSystem behavior, or exclusive writer ownership across the managed and Rust backends. Preserve the initial error if teardown also fails.
