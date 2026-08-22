# Validation operator handoff

Use `openblade_validation_operator` to execute an already-selected validation scope and reduce its output. The primary agent owns the task worktree and decides which scope is required.

## Required inputs

- Absolute dedicated-worktree path and expected full SHA.
- Mode: `affected`, `focused`, `full`, `build-contracts`, or `localization-snapshots`.
- For `affected`: the explicit changed paths or precomputed plan JSON.
- For `focused`: exact project, test filter, and prerequisite restore/build state.
- For localization snapshots: approved culture, configuration, and output directory.

Do not use the primary landing checkout. Reject a dirty worktree unless the primary agent identifies the exact expected dirty paths and confirms they are in scope.

## Execution guidance

- Derive affected plans from `build/CiBuildScope.ps1` and execute them through `build/Run-AffectedCi.ps1` rather than inventing a test matrix.
- Mirror the `full` quality and build-contract commands in `.github/workflows/build.yml`.
- Follow `AGENTS.md` for restore ordering, `--no-restore` use, managed-host named-pipe permissions, and process-scoped PowerShell execution-policy bypass.
- Run `build/Validate-LocalizationResources.ps1` for resource invariants. Run `build/Capture-LocalizationSnapshots.ps1` only when the parent explicitly requests interactive snapshot generation and the environment supports it.
- Build and test output is allowed; source edits, publishing, installation, hardware access, and external mutations are not.

## Required report

Return the observed SHA, exact commands, exit codes, elapsed time, passing/skipped/failed totals where available, generated artifact paths, and the first actionable failure. Separate a confirmed code/test failure from a sandbox, network, missing-tool, or interactive-environment blocker without attempting a code fix.
