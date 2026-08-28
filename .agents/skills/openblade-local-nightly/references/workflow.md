# Local Nightly fallback workflow

This procedure reproduces the release-significant parts of the hosted Nightly workflow when GitHub Actions cannot execute. The checked-in workflow and build scripts remain authoritative; inspect their current contents before each run rather than copying stale values from this reference.

## 1. Establish an unavailable hosted run

1. Read `AGENTS.md`, `VERSIONING.md`, `.github/workflows/nightly.yml`, `.github/workflows/build.yml`, `build/Publish.ps1`, `build/PublishPayloadValidation.ps1`, and `build/NightlyChangelog.ps1` at the selected source commit.
2. Record the landing checkout status without modifying, stashing, or cleaning user work. Require `gh repo view --json nameWithOwner` to return `OSSBlade/openblade-core`, verify that `origin` resolves to that repository rather than a fork, then refresh `origin/main` and tags. Pass `--repo OSSBlade/openblade-core` to every `gh` read and mutation in this workflow.
3. Require a full 40-character source SHA equal to the selected `origin/main` commit. Record the pinned `captures` submodule SHA; only the detached release worktree created later must be clean.
4. Confirm the user authorized GitHub publication. Before dispatch, list Nightly runs and prove that none is queued, requested, waiting, pending, or in progress; the workflow's `cancel-in-progress: true` setting means a new dispatch could otherwise cancel legitimate work.
5. Inspect an existing exact-SHA Nightly run or dispatch `.github/workflows/nightly.yml` on `main` to reserve the workflow run number. Do not dispatch on a different ref.
6. Read the run back with `gh`. Require the `Nightly` workflow, `main` head branch, exact source SHA, positive run number from 1 through 65535, run ID, run attempt, URL, and an infrastructure-unavailable state. Record the billing, capacity, platform, or other GitHub annotation and the number of executed jobs and steps.
7. Stop if any source-controlled job or step failed. Stop if the hosted workflow is still running or can execute normally. Do not manufacture a build revision when GitHub cannot reserve one.
8. Compute the canonical tag from the generated metadata: `v<baseVersion>-nightly.<BuildRevision>`, with `-attempt.<runAttempt>` only when the attempt is not 1. Reject an existing tag or non-draft release. Resume an existing draft only after proving that its target, title, notes, and every uploaded asset belong to these exact inputs.

The reserved identity passed to the build is:

- `BuildRevision`: the hosted Nightly workflow run number;
- `SourceRevision`: the full selected SHA;
- `WorkflowRunId`: `<run-id>.<run-attempt>`.

## 2. Build the exact source locally

Prefer the exact `openblade_release_operator`. Supply only the immutable inputs selected above and mode `nightly-publish`. The operator must leave its detached worktree and artifacts for primary-agent review.

If the user explicitly approves direct fallback after the exact operator is unavailable:

1. Create a fresh detached exact-commit worktree under `C:\tmp`, outside the OneDrive-linked checkout. Use a path that includes the Nightly revision and short SHA.
2. Initialize the pinned `captures` submodule. Prove the parent worktree and submodule are clean, the parent `HEAD` equals `SourceRevision`, and the submodule `HEAD` equals the recorded gitlink.
3. Run the unchanged publisher through process-scoped execution-policy bypass:

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build\Publish.ps1 `
       -Configuration Release `
       -BuildChannel Nightly `
       -BuildRevision <BuildRevision> `
       -SourceRevision <full-source-sha> `
       -WorkflowRunId <run-id>.<run-attempt>
   ```

4. A command-wrapper timeout is not failure while verified .NET or WiX descendants remain active. Inspect the exact process tree and continue bounded monitoring. Stop at the first novel build failure; do not edit source, change arguments, weaken validation, or reuse staging output from a failed OneDrive-path build.

## 3. Run the hosted-equivalent validation lanes

Before any GitHub release mutation, run the full validation mode represented by `.github/workflows/build.yml`: the quality-and-tests lane, the complete build-contract lane, and the installer-validation lane already completed by the unchanged publisher. Do not substitute the normal repository baseline alone; the Nightly workflow requires all three successful results.

Prefer the exact `openblade_validation_operator` in `full` mode on the clean exact-SHA release worktree left by the release operator. Supply its absolute path and expected full SHA. It must mirror the current workflow commands and return exact commands, exit codes, elapsed time, and test totals.

If the user explicitly approves direct validation after that operator is unavailable, mirror the current full lanes exactly:

1. Set the workflow's .NET environment and keep interactive desktop tests enabled. Restore the solution, then run `dotnet format OpenBlade.slnx --verify-no-changes --no-restore --severity error --verbosity minimal`.
2. Run `dotnet build OpenBlade.slnx -p:NuGetAudit=false --no-restore -m:1 --verbosity minimal`.
3. Run the full solution tests with approved host access from the outset: `dotnet test OpenBlade.slnx -p:NuGetAudit=false --no-build --no-restore -m:1 --verbosity minimal`.
4. Restore the solution, the Tray localization-preview configuration, and the disconnected installer renderer exactly as the current build-contract lane does. Run every `tests\Build.Tests\*.Tests.ps1` in sorted name order with process-scoped execution-policy bypass where required.

Require all quality, solution-test, build-contract, and installer-publication lanes to pass. Record unavailable interactive or tool-specific checks separately; do not silently omit them or convert an environment blocker into a pass. Stop before creating a release draft on any code, test, contract, or unexplained environment failure.

## 4. Validate local release artifacts

Require the publisher to exit successfully and independently inspect:

- `artifacts\win-x64\OpenBlade-Setup.exe`;
- `artifacts\win-x64\OpenBlade-Setup.exe.sha256`;
- `artifacts\win-x64\OpenBlade-Nightly.json`;
- the payload and validation reports referenced by the metadata.

Verify that metadata channel, base version, `BuildRevision`, `SourceRevision`, workflow-run identifier, informational version, source URL, installer size, SHA-256, and Authenticode state match the reserved inputs and local files. Require `localPreflightPassed` when present and record every build, test, installer UI, payload, or contract result emitted by the unchanged publisher.

Do not report hosted checks as passing. An unsigned local artifact may be published only when that release state is already allowed and is disclosed in the release notes and final report. Do not pass signing material or invent provenance attestations.

## 5. Create and fill a persistent draft

Re-read the current release contract in `.github/workflows/nightly.yml`. Prepare local-fallback release notes containing the exact version, source link, unavailable hosted run link and status, local validation summary, Authenticode state, prerelease warning, and checksum-verification instruction. Do not call it an automated hosted build.

Create an empty persistent draft targeted at the full source SHA with the computed title and tag, `--prerelease`, and `--latest=false`. Upload the large installer by itself, then upload `OpenBlade-Setup.exe.sha256` and `OpenBlade-Nightly.json`. Do not use `--clobber` unless the existing remote asset has already been proven to be the same artifact; stop on a mismatched name, size, or digest.

Before exposing the release, read the draft back and require:

- exact target SHA, tag, title, draft and prerelease state;
- exactly the three core assets with expected names and byte sizes;
- a remote SHA-256 digest equal to each local file.

Use the GitHub asset digest when available. Otherwise download each remote asset to a fresh temporary directory and hash that download independently. Keep the release as a draft if any check fails.

After the core readback passes, publish the draft and read its canonical `publishedAt` timestamp. Never publish first and assume an interrupted upload rolled back cleanly.

## 6. Attach the exact changelog snapshot

After publication, query all published non-draft prereleases and the current release timestamp using `gh`. Write the release-history manifest to a temporary file, then dot-source `build/NightlyChangelog.ps1` from the exact source worktree and call `Write-OpenBladeNightlyChangelogSnapshot` with:

- the exact worktree and source `CHANGELOG.md`;
- `OpenBlade-Nightly-CHANGELOG.md` as the output;
- the queried release-history manifest;
- metadata base version and build revision;
- current tag and full source SHA;
- the release's canonical `publishedAt` value.

Require a non-empty current section. Upload the snapshot and update the local-fallback release notes with that section.

Changelog tally or attachment failure is nonfatal only because the core prerelease is already published. On tally failure, attach the exact source `CHANGELOG.md` snapshot when possible and add the same explicit warning used by `.github/workflows/nightly.yml`. Report the failure.

## 7. Reconcile the Nightly changelog draft PR

Reproduce the current `update-nightly-changelog` job after publication. Work in a separate clean temporary checkout based on current `origin/main`; never modify the exact release worktree or the landing checkout.

1. Re-fetch `origin/main` and tags, re-read all non-draft prereleases and the current canonical `publishedAt`, and validate that Nightly metadata still matches the selected source SHA and build revision.
2. Generate the updated repository `CHANGELOG.md` with `Write-OpenBladeNightlyChangelogSnapshot`, preserving post-release entries under `Unreleased`. Exit successfully without a branch update when content is already current.
3. Run `git diff --check`, commit only `CHANGELOG.md` with the workflow's conventional subject, and inspect `refs/heads/docs/nightly-changelog-automation` on `OSSBlade/openblade-core`.
4. If the remote branch is absent, push the generated commit to it. If it exists, push only with `--force-with-lease=refs/heads/docs/nightly-changelog-automation:<observed-remote-sha>`. Never use an unguarded force push and never push to `main`.
5. Create or update the one draft documentation pull request with the exact version title, version-impact fields, source and hosted-run evidence, generated marker, and `documentation` label used by `.github/workflows/nightly.yml`. Read back the PR number, draft state, branch, commit, title, body, and label.

A reconciliation failure does not retract the already published prerelease, but it leaves the local fallback incomplete. Report the blocker and preserve the temporary checkout and remote state; do not delete or rewrite uncertain work.

## 8. Perform final remote readback

Fetch the published tag and prove it resolves to the full source SHA. Read the release through both `gh release view` and the GitHub API. Require:

- `draft=false`, `prerelease=true`, the exact title, tag, target, source and hosted-run links;
- the expected local-fallback disclosure and signature state in the notes;
- exactly four assets: installer, checksum, Nightly metadata, and Nightly changelog;
- byte-for-byte size and SHA-256 agreement between every local and independently read remote asset.

If the changelog fallback could not be attached, report the published three-asset exception explicitly. Do not describe a release as fully verified while any remote digest, target, note, or asset remains unchecked.

Report that hosted CI validation and GitHub artifact attestations were unavailable. Separate that limitation from the local build and validation evidence.

Also require the Nightly changelog draft PR to point at the generated commit, or prove that the repository changelog was already current and no update was needed.

## 9. Preserve recoverability

Leave the exact build worktree, artifacts, temporary evidence, and any failed draft in place until the user accepts the readback or explicitly requests cleanup. Never delete a tag, release, asset, worktree, or artifact merely to make a retry look clean.

For later cleanup, resolve and verify the exact `C:\tmp` worktree path, prove the parent and `captures` submodule are clean, deinitialize the submodule, remove only that registered worktree, and confirm the path is no longer registered. Keep cleanup separate from release acceptance.
