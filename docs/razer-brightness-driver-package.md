# Razer brightness driver package

OpenBlade's Windows-native brightness path uses the existing, signed Razer
Consumer Control stack on the admitted Razer Blade 16 RZ09-0581. The ordinary
installer does not currently install that stack. When it is missing or does not
match the admitted build, OpenBlade keeps using WMI and its own brightness
flyout.

`build/Export-RazerBrightnessDriverPackage.ps1` creates a private, exact export
with Windows PowerShell 5.1 from a machine that already has the validated
stack. It locates each installed package by the hash of its published INF
rather than its machine-local `oem###` name, calls Windows
`pnputil /export-driver`, and then verifies the complete three-file package,
SHA-256 hashes, and Microsoft hardware signatures. The output must be outside
the repository.

```powershell
.\build\Export-RazerBrightnessDriverPackage.ps1 `
    -OutputDirectory C:\tmp\Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76
```

The resulting deterministic ZIP contains five original Razer packages: the
physical USB lower filter, virtual Consumer Control function driver, control
device driver, and the keyboard and mouse collection filters. Windows inbox HID
dependencies are referenced by the original INFs and are not copied. Generated
PNF files and machine-specific device instances are excluded.

The archive is not yet an installer. Clean-machine installation order, reboot,
repair, rollback, and removal remain validation gates. Do not install the
package on another Blade model or weaken OpenBlade's exact device and binary
checks to make it load.

The exported files remain Razer property and are not covered by OpenBlade's GPL
license. Before attaching the archive to a public release, retain written Razer
authorization covering the exact files and distribution method, and ship the
archive's `THIRD-PARTY-NOTICE.txt` unchanged.
