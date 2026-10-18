# Legacy protocol evidence

This directory contains the sanitized, immutable evidence that OpenBlade still
uses for protocol tests and exact-device production admission. The files were
imported from `OpenBladeProject/openblade-captures` at revision
`4f2bf1565c7557439760db7e45104bb1e8b62bfd` when that repository became a
private archive.

[`provenance.json`](provenance.json) records every imported file's original
path, source revision, destination, and SHA-256. The imported JSON bytes remain
unchanged so existing evidence and admission hashes continue to identify the
same reviewed artifacts. Historical `captures/...` strings in admission
manifests are provenance identifiers; tests resolve them under
`evidence/legacy-captures/`.

The obsolete USBPcap collection tools are not included. Use
[Synapse Observer](https://github.com/OpenBladeProject/synapse-observer) to
collect new model evidence, then review and add only the sanitized artifacts
needed by OpenBlade. Keep new evidence outside `legacy-captures` and record its
own source and provenance; `provenance.json` is fixed to the legacy source
revision above.

The source revision contained no separate license file. Its repository and
revision attribution is retained in the provenance map; see OpenBlade's
[license](../LICENSE) for the terms of this repository.
