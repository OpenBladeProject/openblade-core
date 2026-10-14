# Microsoft Store EXE submission

OpenBlade uses the Microsoft Store's existing EXE route. The Store invokes the
offline WiX Burn bundle built with `build/Publish.ps1 -OfflineInstaller`; OpenBlade is not
repackaged as MSIX.

## Package fields

Use `build/store/OpenBlade.StoreSubmission.json` as the checked-in source for
the fields that must stay synchronized with the release:

| Partner Center field | Value |
| --- | --- |
| App type | EXE |
| Architecture | x64 |
| Language | en-US |
| Installer parameters | `/install /quiet /norestart` |
| Minimum operating system | Windows 11 version 24H2 x64, build 26100 or later |
| Supported hardware | Razer Blade 16 (2026), RZ09-0581, USB `1532:02E0`; Blade 16 (2025), RZ09-0528, USB `1532:02C6` |

The submitted package URL must be HTTPS, immutable, and versioned. Never replace
the file behind an existing URL. A suitable shape is
`https://<release-host>/vX.Y.Z/OpenBlade-Setup.exe`.

The checked-in public privacy-policy URL is
<https://openblade.sh/privacy.html>. Keep that absolute HTTPS contract
synchronized with the deployed documentation and Partner Center.

## Package behavior

- The bundle is an offline installer: every package and payload is embedded,
  including the pinned, Microsoft-signed .NET 10 Desktop Runtime and Visual C++ runtime required by the
  unpackaged Windows App SDK deployment.
- Interactive direct downloads show OpenBlade Setup and download .NET when needed. Partner Center supplies
  `/install /quiet /norestart`, which explicitly selects installation, suppresses
  installer UI, and prevents an automatic
  restart.
- Partner Center owns that quiet invocation and cannot perform the
  caller-controlled Tray relaunch used by direct automation. A Store install
  or upgrade can therefore leave the Tray and Session Agent stopped in the
  current sign-in session. The Store-specific recovery remains tracked in
  [issue #25](https://github.com/OSSBlade/openblade-core/issues/25); the
  interactive Launch action from #24 does not solve it.
- Windows may show UAC because OpenBlade installs a per-machine LocalSystem
  service. UAC is the only permitted Store-install prompt.
- Exit code `0` means installation succeeded. Until end-to-end Store lifecycle
  testing proves additional stable Burn exit codes, leave optional Partner
  Center mappings unset and treat nonzero values as installation failures.
- The stable Burn bundle ID and provider key are both `OSSBlade.OpenBlade`;
  the explicit upgrade-family code is
  `{FF859CA9-D69F-4995-A2AD-69E540E45724}`. Higher product versions compare
  against the installed marker version and replace the same bundle family.
- The bundle refuses platforms older than Windows 11 version 24H2 build 26100
  and non-x64 Windows installations, matching the tray's declared supported
  platform.

## Certification notes

Use the following as the starting point for Notes for certification:

> OpenBlade is a free, open-source, Razer-only hardware utility. This release
> supports the Razer Blade 16 (2026), RZ09-0581, USB 1532:02E0, and the
> Blade 16 (2025), RZ09-0528, USB 1532:02C6. Available controls vary by model;
> the compatibility page lists them. The app installs its own LocalSystem
> service, which exclusively owns
> the supported laptop-control HID handle and exposes typed local IPC to the
> per-user tray and Session Agent. It does not install a kernel driver, require
> the Synapse application, or download executable content. The optional
> Windows-native brightness flyout uses only an exact admitted Razer driver
> stack when already present and otherwise falls back to Windows WMI.
> The per-user tray can request
> public release metadata from GitHub at a user-selected interval; Manual only
> disables automatic checks. For supported models, the tray can also request
> published updater versions from `mysupport.razer.com` at a separately selected
> interval that defaults to Manual only. The LocalSystem service remains offline,
> and no profiles, diagnostics, or current hardware state are sent with either
> check; the model-specific Razer support URL identifies the supported model.
> Unsupported hardware is detected and reported without attempting hardware
> writes. Hardware and firmware limitations are disclosed in the Store listing
> and compatibility documentation.

Certification must be given a supported test device or another testing
arrangement accepted by Microsoft. Do not add a production bypass that fakes
device admission for certification.

## Local preflight

`build/Publish.ps1` produces
`artifacts/win-x64/OpenBlade-Store-Readiness.json`. The report proves:

- the Store package fields match the checked-in contract;
- all payloads remain embedded in the validated Burn bundle;
- every third-party PE already has a valid Authenticode signature;
- every unsigned PE is a first-party OpenBlade binary awaiting release signing;
- the stable Burn upgrade family is present in the compiled manifest.

## Release VM gate

Before publishing a version, use a clean Windows 11 x64 VM for the lifecycle
test that authoring and manifest checks cannot simulate:

1. Install the previous public installer and verify one OpenBlade entry in
   Installed apps, a healthy service, a running Session Agent, and a usable
   tray.
2. Save a recognizable profile, then run the new higher-version installer.
   Verify it replaces the installed files, preserves the profile, leaves one
   Installed apps entry, and updates the installed-version marker under
   `HKLM\SOFTWARE\OSSBlade\OpenBlade\Installer`.
3. Repeat the direct upgrade from a normal-integrity validation harness with
   `/install /quiet /norestart`. After setup succeeds, have that same harness
   start `%ProgramFiles%\OpenBlade\OpenBlade.Tray.exe`. Verify the upgrade
   replaced the files, preserved the profile, retained one Installed apps
   entry, updated the installed-version marker, produced no application prompt
   or automatic restart, and left exactly one usable Tray and one Session
   Agent. This proves the caller-owned quiet path only; it does not close the
   Partner Center-owned Store gap in issue #25.
4. Repair, uninstall, and reinstall the new version. Verify the Microsoft
   Visual C++ prerequisite works both when absent and when the VM has an older
   14.x runtime below 14.50.35719.
5. Exercise `uninstall --recover-orphaned-bundle` only in a snapshot where the
   Burn registration was deliberately removed while the OpenBlade ownership
   marker remained; verify a failed binary removal restores that marker.

For a signed release, pass `-SigningThumbprint` and `-VerifyPrivacyPolicy` to
`build/Publish.ps1`. Publishing signs the first-party payload before WiX embeds
it, then uses WiX's detached-engine and final-bundle signing hooks. The privacy
switch requires the canonical URL to return HTTP 200 with `text/html` and the
rendered policy heading. Publish the installer at an immutable HTTPS URL and
complete the external Partner Center fields.
