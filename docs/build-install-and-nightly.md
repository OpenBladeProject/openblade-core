# Build, installation, and Nightly releases

This document covers production publishing, installation, recovery, and the
Nightly release workflow. For ordinary source builds, use the commands in the
main [README](../README.md#build-and-test).

## Publish artifacts

Create the graphical installer, self-contained `win-x64` production bundle,
and separate developer tools:

```powershell
.\build\Publish.ps1
```

- Graphical installer: `artifacts\win-x64\OpenBlade-Setup.exe`
- Unpacked production bundle and command-line setup:
  `artifacts\win-x64\OpenBlade`
- Developer tools: published separately by the same script

Release signing is optional during development. For a public signed build,
supply the certificate thumbprint to `Publish.ps1`. The script signs first-party
payload files before WiX embeds them, then signs the detached Burn engine and
the final installer.

## Interactive installation

- Double-click `OpenBlade-Setup.exe` and choose Install. Windows requests
  administrator approval only when setup is ready to make system changes.
- The installer follows the Windows app theme. Choose **Show details** during
  install, repair, or removal to see the files, registry entries, shortcuts,
  and services being changed.
- After a successful install or repair, choose **Launch OpenBlade**. The Tray
  starts the Session Agent, and both run in the normal user session.
- Do not start the installer with **Run as administrator**. The Tray and Session
  Agent would inherit elevation. Close them and start OpenBlade normally if
  that happens.

## Quiet installation

- Quiet installs suppress both installer UI and application launch.
- After `/quiet` returns successfully, the invoking automation must start the
  installed `OpenBlade.Tray.exe` from its original normal-integrity process and
  confirm that the Tray and Session Agent are running.
- This caller-controlled relaunch does not apply to the Microsoft Store EXE
  channel because Partner Center owns the quiet invocation. The remaining gap
  is tracked in [issue #25](https://github.com/OSSBlade/openblade-core/issues/25).

## Versioned development builds

Use a monotonically increasing Nightly revision when installing several exact
development builds without changing the base product version:

```powershell
.\build\Publish.ps1 -BuildChannel Nightly -BuildRevision 42 -SourceRevision (git rev-parse HEAD)
```

- When interactive setup finds exactly one cached bundle with the same version,
  it offers **Repair** to overwrite the installed files with the incoming
  payload or **Uninstall** to run the installed bundle's removal path.
- Quiet `/install` keeps treating an equal version as a version conflict because
  it cannot choose a maintenance action on the user's behalf. Use `/repair` or
  `/uninstall` explicitly for unattended maintenance.
- Use a higher Nightly revision for each exact-build validation. Do not install
  different Stable artifacts carrying the same product version.

## Duplicate bundle registrations

Setup stops before Apply when it detects several related OpenBlade bundle
registrations. Older installers can recursively launch one another during Burn
removal and deadlock.

- The guard also applies to a mixed set with one older and one newer
  registration because there is no single predecessor to replace.
- Interactive setup reports the conflict after detection instead of offering
  Install or Repair.
- If setup offers Uninstall for its own registered bundle, use that option
  first.
- Remove remaining duplicate entries through Windows installer recovery before
  retrying the newer setup.

## Command-line setup and recovery

The unpacked production bundle remains available at
`artifacts\win-x64\OpenBlade` for development and recovery. Run its setup from
an elevated terminal when needed:

```powershell
.\artifacts\win-x64\OpenBlade\OpenBlade.Setup.exe install
```

Uninstall through Windows Settings or rerun the graphical installer. Both
preserve saved profiles and preferences. The standalone setup also supports
`uninstall --purge` to delete saved data when it owns the installation.

If the graphical installer is gone but its ownership marker remains, use:

```powershell
.\artifacts\win-x64\OpenBlade\OpenBlade.Setup.exe uninstall --recover-orphaned-bundle
```

## Microsoft Store channel

- The future Microsoft Store EXE listing uses the same offline installer.
- Store installs use `/install /quiet /norestart`; direct downloads retain the
  graphical UI.
- The Store caller cannot perform the normal-integrity relaunch required after
  a quiet install, so lifecycle support remains incomplete until
  [issue #25](https://github.com/OSSBlade/openblade-core/issues/25) is resolved.

See [Microsoft Store submission](microsoft-store-submission.md) for the package
contract, certification notes, hardware disclosure, and remaining external
release steps.

## Nightly workflow

- The `Nightly` GitHub Actions workflow builds the latest committed `main`
  every day at 09:17 UTC and can also be started manually.
- It runs the formatting, build, test, authoring, and production-publish gates
  used by CI, then retains the Actions artifact for 14 days.
- Each successful build is a GitHub prerelease with its installer, checksum,
  metadata, and changelog snapshot from the exact build commit.
- After publication, the workflow opens or updates a draft documentation pull
  request that records missing Nightly boundaries and preserves newer
  `Unreleased` entries from `main`.
- Nightlies appear on the Releases page and in the app only when the
  **Nightly** update channel is selected.

Nightlies are test builds. Verify `OpenBlade-Setup.exe` against the included
SHA-256 file before installation.

### Attestations and signing status

GitHub build provenance is optional because artifact attestations are not
available for every repository visibility and billing plan. After confirming
support, maintainers can set `OPENBLADE_ENABLE_ATTESTATIONS` to `true` and
verify an enabled attestation with:

```powershell
gh attestation verify .\OpenBlade-Setup.exe --repo OSSBlade/openblade-core
```

The included JSON identifies the exact commit, ordered Nightly version, build
time, workflow run, installer hash, and Authenticode status. Unless the workflow
is connected to the protected release-signing certificate, the installer is
expected to report `NotSigned`, and Windows may show an unrecognized-publisher
warning.

### Changelog publication

- The changelog job requires the repository setting **Allow GitHub Actions to
  create and approve pull requests**.
- Its token is limited to repository contents and pull requests in that job.
- If the setting is disabled, the Nightly release remains published. Enable the
  setting and rerun the failed changelog job.
- Changelog tally errors do not block publication. The release attaches the
  exact source changelog and reports the tally failure instead of withholding
  the installer and checksum.
