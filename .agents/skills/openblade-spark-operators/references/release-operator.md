# Release operator handoff

Use `openblade_release_operator` only for a local artifact build or artifact audit that the user has already requested. The primary agent chooses and supplies every release-significant input.

## Required inputs

- Full immutable source SHA.
- Mode: `stable-publish`, `nightly-publish`, or `nightly-artifact-audit`.
- For a nightly publish: positive `BuildRevision`, exact `SourceRevision`, and optional non-secret workflow-run identifier.
- Expected artifact-report destination or an instruction to report in the agent result only.

The operator must reject a branch name, moving ref, missing SHA, missing nightly revision, or a `SourceRevision` that differs from the selected SHA.

## Execution boundary

The operator follows the production-publishing section of `AGENTS.md`:

- create a fresh detached exact-commit worktree under `C:/tmp`;
- initialize the pinned `captures` submodule;
- run the unchanged `build/Publish.ps1` through process-scoped PowerShell execution-policy bypass when needed;
- monitor active .NET and WiX processes before treating a wrapper timeout as failure;
- leave generated artifacts and the exact worktree available for primary-agent review.

Do not pass signing credentials or `-VerifyPrivacyPolicy`. Do not install, launch product processes, request UAC, push, tag, publish a GitHub release, edit the changelog, or delete the worktree.

## Required report

Return:

- requested and observed SHA;
- worktree path and clean preflight result;
- exact command and exit code;
- first actionable failure with a short relevant log excerpt, if any;
- artifact paths, byte sizes, SHA-256 values, product/file versions, signature status, and readiness metadata when present;
- any approval or environment blocker.

Stop at the first novel failure. Do not patch, retry with changed arguments, or reinterpret a failed gate as success.
