---
title: Record Synapse-free THX runtime separately from fresh installation
author: Jack
date: 2026-10-21
branch: docs/thx-synapse-free-reports
touches:
  - docs/thx-audio.md
  - docs/thx-installation-investigation.md
---

## Decided
Record two community reports of THX working with Synapse absent using audio components already installed. Issue #808 identifies the primary report as RZ09-0528 on 0.39.0-nightly.97+ef7532fe with audible preset/EQ differences; its follow-up confirms Synapse was reinstalled only after the successful test. The second report is maintainer-relayed and described only as a Blade 2025. Neither user used Install THX audio. Link the source comments and keep fresh installation explicitly untested.

## Tried and ruled out
Synapse-free runtime success does not validate OpenBlade's installer, a pristine audio stack or a controlled Synapse-uninstall sequence. Do not infer an exact RZ09 model, build, output, individual control or legacy compatibility from the second report. Do not keep older blanket claims that rznb11 audible processing has never been reported, or erase the separate local RZ09-0581 round-trip evidence.

## Watch out
These are scoped community reports, not a production-ready promotion or evidence of every control and output. RZ09-0530 physical runtime evidence, legacy endpoint-binding and installation blockers, and all fresh-install acceptance results remain outstanding. The prior learning describing both reporters' Synapse state as unknown is superseded by the maintainer's clarification and the public #808 reply.
