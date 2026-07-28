# Microsoft Store EXE submission

OpenBlade uses the Microsoft Store's existing EXE route. The Store invokes the
same offline WiX Burn bundle used for direct downloads; OpenBlade is not
repackaged as MSIX.

## Package fields

Use `build/store/OpenBlade.StoreSubmission.json` as the checked-in source for
the fields that must stay synchronized with the release:

| Partner Center field | Value |
| --- | --- |
| App type | EXE |
| Architecture | x64 |
| Language | en-US |
| Installer parameters | `/quiet /norestart` |
| Minimum operating system | Windows 11 x64 |
| Supported hardware | Razer Blade 16 (2026), RZ09-0581, USB `1532:02E0` |

The submitted package URL must be HTTPS, immutable, and versioned. Never replace
the file behind an existing URL. A suitable shape is
`https://<release-host>/vX.Y.Z/OpenBlade-Setup.exe`.

The public privacy-policy URL must point to the deployed `privacy.html` page in
the OpenBlade documentation site.

## Package behavior

- The bundle is an offline installer: every package and payload is embedded.
- Interactive direct downloads show WixStdBA. Partner Center supplies
  `/quiet /norestart`, which suppresses installer UI and prevents an automatic
  restart.
- Windows may show UAC because OpenBlade installs a per-machine LocalSystem
  service. UAC is the only permitted Store-install prompt.
- Exit code `0` means installation succeeded. Until end-to-end Store lifecycle
  testing proves additional stable Burn exit codes, leave optional Partner
  Center mappings unset and treat nonzero values as installation failures.
- The stable Burn bundle ID and provider key are both `OSSBlade.OpenBlade`.
  Higher product versions upgrade the same bundle family instead of installing
  side by side.

## Certification notes

Use the following as the starting point for Notes for certification:

> OpenBlade is a free, open-source, Razer-only hardware utility. This release
> supports only the 2026 Razer Blade 16 model RZ09-0581 with USB identity
> 1532:02E0 and the firmware combinations listed on the OpenBlade compatibility
> page. The app installs its own LocalSystem service, which exclusively owns
> the supported laptop-control HID handle and exposes typed local IPC to the
> per-user tray and Session Agent. It does not install a kernel driver, depend
> on Synapse, or download executable content. The per-user tray can request
> public release metadata from GitHub at a user-selected interval; Manual only
> disables automatic checks. The LocalSystem service remains offline, and no
> hardware state, profiles, or diagnostics are sent with an update check.
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

Before Partner Center submission, rerun the preflight with
`Write-OpenBladeStoreReadinessReport -RequireSignatures`, deploy the privacy
page, publish the installer at an immutable HTTPS URL, and complete the external
certification fields.
