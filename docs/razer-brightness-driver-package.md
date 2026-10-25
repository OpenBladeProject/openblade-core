# Razer brightness driver package

English | [简体中文](zh-CN/razer-brightness-driver-package.md) | [Русский](ru-RU/razer-brightness-driver-package.md)

OpenBlade can use the signed Razer Consumer Control stack already installed on
either of the two Blade 16 models listed below. Windows then owns the brightness change and native
flyout. About > Device information reports whether the exact model's admitted
runtime path is available. If it is unavailable, the brightness keys continue
to use WMI and the OpenBlade flyout.

The ordinary OpenBlade installer does not install or remove Razer drivers.

## Runtime hardware scope

The two native-brightness backends use separate, model-specific stacks. OpenBlade checks
the full device ancestry, service name, actual installed INF/service binding,
Windows catalog trust and file membership before using either one. Compatible
driver updates do not require new hashes in OpenBlade. Observed content
fingerprints detect replacement and invalidate stale admission. Device and file
identity checks run before each brightness write, including when reusing a
discovered endpoint. Signature trust results use a bounded short-lived cache:

| Laptop | USB PID | Device service |
|---|---|---|
| Razer Blade 16 (2026), RZ09-0581 | `02E0` | `RzDev_02e0` |
| Razer Blade 16 (2025), RZ09-0528 | `02C6` | `RzDev_02c6` |

No stack is universal across Razer Blade laptops. The RZ09-0581 stack contains
four model-specific signed INFs plus the shared control package:

| Package | Exact hardware match | Role |
|---|---|---|
| `rzdevu_02e0_dkm.inf` | `USB\VID_1532&PID_02E0&MI_01` | Physical-device filter and virtual-device enumerator |
| `rzdevu_02e0_vcon.inf` | `RZVIRTUAL\VID_1532&PID_02E0&MI_00&COL03` | Virtual Consumer Control device |
| `rzdevu_02e0_kbd.inf` | `HID\VID_1532&PID_02E0&MI_01&COL01` | Keyboard collection filter |
| `rzdevu_02e0_mou.inf` | `HID\VID_1532&PID_02E0&MI_02` | Mouse collection filter and control-device enumerator |
| `rzcommonu.inf` | `RAZER\DeviceContol` | Shared Razer control child |

The last identifier is spelled `DeviceContol` in the signed Razer INF. The
originally inspected `rzcommonu.inf`, SHA-256
`DD3C8D65C7C3CCF2093FE70FB21FB8DC724E837880566BAE629B3104A5B1B37B`,
contains `%RzCommon.DeviceDesc% = RzCommon_Device,RAZER\DeviceContol`
verbatim. The generic RzCommon component does not make the full stack generic.
The other four packages, the `RzDev_02e0` service and binary name, and their
catalogs are specific to PID `02E0`. Editing an INF for a different PID would
invalidate its signed catalog and would not prove that the binary supports
that hardware.

RZ09-0528 uses its own signed Razer stack when it is already installed:
`rzdevu_02c6_{dkm,vcon,kbd,mou}.inf`
with the `RzDev_02c6` service. The originally inspected packages used the same
`RzCommon.sys` build and `RZCONTROL`/`RZVIRTUAL` device shape on PID `02C6`. The inspected
`RzDev_02c6.sys` has SHA-256
`307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82`.
This is source provenance. Runtime admission checks every identity against PID
`02C6` and the trusted API contract. The export tooling and catalog entry in
this guide remain specific to `02E0`; OpenBlade does not bundle either stack.
Every other model needs its own signed-package inventory and hardware
validation.

Razer support pages:

- [Razer Blade 16 (2026), RZ09-0581x](https://mysupport.razer.com/app/answers/detail/a_id/20585/)
- [Razer Blade 16 (2025), RZ09-0528x BIOS support](https://mysupport.razer.com/app/answers/detail/a_id/15235/)

The exported RZ09-0581 archive, its sanitized manifest, and the release record
that binds the two live in the separate
[`blade-driver-catalog`](https://github.com/OSSBlade/blade-driver-catalog),
together with read-only export instructions for other Razer devices. The
catalog publishes the archive as a prerelease; clean installation is still
pending. Running Synapse once installs the same stack. An entry in that
catalog is evidence, not OpenBlade support.
Each model still needs independent runtime and hardware validation before core
admits it.

## Export the admitted RZ09-0581 package

`build/Export-RazerBrightnessDriverPackage.ps1` creates a private, exact export
with Windows PowerShell 5.1 from a machine that already has the validated
stack. It locates each installed package by the hash of its published INF,
independent of its machine-local `oem###` name, calls Windows
`pnputil /export-driver`, and then verifies the complete three-file package,
SHA-256 hashes, and Microsoft hardware signatures. The output must be outside
the repository.

The archive's `manifest.json` uses manifest type
`OpenBlade.RazerDriverPackageArchive`. Its `catalogReference` names
`OSSBlade/blade-driver-catalog` and
`devices/rz09-0581/1532-02e0/1.0.0.78-1.0.0.76/manifest.json`. It deliberately
does not embed the catalog manifest hash because the catalog records the outer
package archive hash. The checked release attestation binds the catalog
manifest path and SHA-256 to the archive asset name and SHA-256 without making
the two files hash each other.

```powershell
.\build\Export-RazerBrightnessDriverPackage.ps1 `
    -OutputDirectory C:\tmp\Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76
```

The resulting deterministic ZIP contains five original Razer packages: the
physical USB lower filter, virtual Consumer Control function driver, control
device driver, and the keyboard and mouse collection filters. Windows inbox HID
dependencies are referenced by the original INFs and are not copied. Generated
PNF files and machine-specific device instances are excluded.

Do not install the package on another Blade model or weaken OpenBlade's exact
device and binary checks to make it load.

## Native Windows installation path

Windows includes PnPUtil for staging signed packages in Driver Store and
installing them on matching devices. Microsoft documents `/add-driver`,
`/install`, `/subdirs`, and `/reboot` in the
[PnPUtil command reference](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax).

The sequence below is for clean-machine validation on a recoverable RZ09-0581.
It is not yet an end-user installation recipe. The stack filters the integrated
keyboard and mouse paths, restarts parts of the USB device tree, and may require
a reboot. Use AC power, keep an external USB keyboard and mouse connected, and
make sure recovery access works before testing it.

Before the first mutation, verify the target and package from an elevated
PowerShell window:

1. `HKLM:\HARDWARE\DESCRIPTION\System\BIOS` must report Razer as the
   manufacturer and `Blade 16 - RZ09-0581` as `SystemProductName`.
2. Windows must report one connected `USB\VID_1532&PID_02E0&MI_01` device and
   one connected `USB\VID_1532&PID_02E0&MI_02` device. Stop on missing,
   duplicate, or different identities.
3. The ZIP name and SHA-256 must match the checked release attestation. The
   same attestation must identify the catalog manifest by repository path and
   SHA-256.
   After extraction, every file must match `SHA256SUMS.txt`, and every INF,
   CAT, and SYS signature must be valid and issued by Microsoft Windows
   Hardware Compatibility Publisher. On a validation host with SignTool, use
   `signtool verify /kp /c <catalog> <INF-or-SYS>` to prove catalog membership.
   Microsoft documents `/c` in the
   [SignTool reference](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/signtool).

Create a journal outside the package and repository before staging anything.
Record the BIOS identity, connected matching instances, the output of
`pnputil /enum-drivers /files`, their current bindings and stacks, and the
current `RZVIRTUAL` and `RZCONTROL` tree. Keep the original output, not a
hand-written summary.

The helper below appends every PnPUtil command, output, and exit code to the
journal. Set `$packageRoot` and `$journalDirectory` first. Stop after any
nonzero exit code. Do not retry an ambiguous result.

```powershell
$packageRoot = 'C:\path\Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76'
$journalDirectory = 'C:\path\openblade-razer-driver-journal'
$pnp = "$env:WINDIR\System32\pnputil.exe"
$transcript = Join-Path $journalDirectory 'pnputil-transcript.txt'
New-Item -ItemType Directory -Path $journalDirectory -ErrorAction Stop | Out-Null

function Invoke-PnpLogged {
    param([Parameter(Mandatory)][string[]]$Arguments)

    "`r`n> pnputil $($Arguments -join ' ')" |
        Out-File -LiteralPath $transcript -Append -Encoding utf8
    & $pnp @Arguments 2>&1 |
        Tee-Object -FilePath $transcript -Append
    $exitCode = $LASTEXITCODE
    "Exit code: $exitCode" |
        Out-File -LiteralPath $transcript -Append -Encoding utf8
    if ($exitCode -ne 0) {
        throw "PnPUtil failed with exit code $exitCode."
    }
}
```

First stage all five signed packages without binding them:

```powershell
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_vcon\rzdevu_02e0_vcon.inf")
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzcommonu\rzcommonu.inf")
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_dkm\rzdevu_02e0_dkm.inf")
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_kbd\rzdevu_02e0_kbd.inf")
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_mou\rzdevu_02e0_mou.inf")
```

After each command, copy the newly reported published name and package identity
into the journal. Mark whether it existed in the pre-install inventory. This is
the ownership proof used by rollback.

After all five packages stage successfully, bind them in dependency order:

```powershell
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_dkm\rzdevu_02e0_dkm.inf", '/install')
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_vcon\rzdevu_02e0_vcon.inf", '/install')
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_kbd\rzdevu_02e0_kbd.inf", '/install')
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_mou\rzdevu_02e0_mou.inf", '/install')
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzcommonu\rzcommonu.inf", '/install')
```

Do not add `/reboot` to these commands during validation. If Windows requests a
restart, stop for a deliberate reboot after the journal is safely stored.
PnPUtil does not force a lower-ranked driver onto a device, so a zero exit code
does not replace post-install inspection.

After installation, verify the exact `RZVIRTUAL` and `RZCONTROL` children,
their services and stacks, and the absence of Device Manager problems. Restart
OpenBlade and check About > Device information. The Razer brightness driver row
should read `Available · Windows OSD`. A physical brightness press, hold, and
release must change brightness and use the Windows flyout.

Do not use broad device removal, `/force`, or class-wide restart commands. A
rollback may remove only published packages that a durable pre-install journal
proves were introduced by that test. Remove those packages in this order:
RzCommon, VCon, mouse, keyboard, then DKM. Use
`pnputil /delete-driver <journal-owned-oem.inf> /uninstall` and stop if Windows
reports that a package is in use or a reboot is required.

The SnapTap **Install driver** action implements these staging and binding steps
in a separate elevated helper. It also has a separately pinned RZ09-0528/02C6
recipe; no 02E0 path or artifact is substituted on that model. The 02C6 mouse
filter's ControlDevice=1 declaration creates the required control child.
Both recipes preserve their dependency order and independent readback. The
RZ09-0528 recipe remains source-backed experimental pending physical setup and
recovery validation. It verifies the pinned catalog package and records
operations in `%ProgramData%\OpenBlade.DriverInstallation\installation.jsonl`.
A pending journal blocks another installation attempt, including after a restart.
Preserve that journal and inspect the current bindings before manual recovery;
do not delete it or repeat installation to bypass an uncertain operation.
Offline tests do not replace the clean-install checks below.

Before publishing a one-click installer, validate two clean RZ09-0581 installs,
failure recovery after each package, reverse rollback with the integrated
keyboard and touchpad restored, idempotent repair, reboot and sleep cycles, and
the physical brightness hold and release path. Until those checks pass, use the
native commands only as a controlled validation procedure.

The exported files remain Razer property and are not covered by OpenBlade's GPL
license. Before attaching the archive to a public release, retain written Razer
authorization covering the exact files and distribution method, and ship the
archive's `THIRD-PARTY-NOTICE.txt` unchanged.
