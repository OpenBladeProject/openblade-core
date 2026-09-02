---
name: openblade-local-nightly
description: Create and verify an OpenBlade Nightly locally only when the hosted Nightly workflow cannot execute and the user explicitly requests the fallback. Do not use for ordinary CI, stable releases, installation, or local development builds.
---

# OpenBlade Local Nightly

Use the hosted `.github/workflows/nightly.yml` workflow whenever it can run. This skill is an outage fallback that preserves the same immutable source identity, build inputs, release assets, changelog snapshot, and evidence without claiming unavailable hosted validation.

## Confirm authority and scope

Before any mutation, confirm that the user explicitly requested a local Nightly build and GitHub prerelease publication because hosted CI is unavailable. A request to prepare, inspect, or build does not authorize dispatching a workflow, creating a tag or release, uploading assets, or publishing a draft.

Keep these boundaries throughout the run:

- Use a full immutable commit SHA that is the selected `origin/main` commit. Never accept a branch name, moving ref, dirty source, or inferred release input.
- Do not edit source, change `OpenBladeVersion`, patch a failed gate, install artifacts, request UAC, launch OpenBlade, touch hardware, pass signing credentials, or claim an attestation.
- Treat a source or test failure as a release blocker, not as CI unavailability. Hosted jobs with zero executed steps and an infrastructure or billing annotation are unavailable, not green.
- Keep local artifact creation separate from GitHub publication. Stop at the user's authorized boundary.
- Reproduce the guarded `docs/nightly-changelog-automation` draft-PR update after publication. Never push directly to `main` or replace that branch without the current remote SHA as a force-with-lease guard.

## Use the exact release operator

Route the artifact-build phase through the exact `openblade_release_operator` custom agent with mode `nightly-publish`, full SHA, positive `BuildRevision`, matching `SourceRevision`, and the reserved workflow-run identifier. Route the hosted-equivalent full quality and build-contract lanes through the exact `openblade_validation_operator` on the resulting exact-SHA worktree.

If the runtime cannot select either exact named agent, or an operator is unavailable or usage-limited, stop before its phase and ask for explicit approval to execute that same bounded build or validation directly. A generic subagent, task label, or model override is not an OpenBlade operator. The primary agent always owns GitHub mutations and final acceptance.

## Run the fallback

Read [references/workflow.md](references/workflow.md) completely before acting. Follow its phases in order and stop at the first novel failure. Preserve any recoverable draft release and exact worktree for inspection instead of deleting or overwriting uncertain state.

## Report the result

Return the reserved hosted run and its unavailable-state evidence, exact source SHA, Nightly revision and tag, build worktree, exact publish command and exit code, validation totals, artifact paths and SHA-256 values, signature state, release URL and target, remote asset readback, changelog outcome, and every unavailable or skipped hosted gate. State clearly whether the prerelease remains a draft or is publicly published.
