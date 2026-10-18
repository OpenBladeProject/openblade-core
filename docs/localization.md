# Localization

OpenBlade keeps its source language in four resource catalogs:

- `src/OpenBlade.Tray/Strings/en-US/Resources.resw` contains WinUI text,
  tooltips, and accessibility names.
- `src/OpenBlade.Tray.Core/Properties/Resources.resx` contains UI-independent
  labels, status text, and presentation summaries used by the tray.
- `build/installer/OpenBlade.Installer.UI/Properties/Resources.resx` contains
  the graphical installer text.
- `src/OpenBlade.Setup/Properties/Resources.resx` contains native uninstall
  prompts and shell-visible setup text.

Tray.Core remains independent of WinUI by using ordinary managed resources.
Protocol values, persisted values, diagnostics, and logs stay language neutral.

Production supports these Windows UI cultures:

- English (United States): `en-US`
- Simplified Chinese (China): `zh-CN`
- Russian (Russia): `ru-RU`

Settings > Appearance > App language offers `Follow Windows`, English,
Simplified Chinese, and Russian. `Follow Windows` is the default and uses the
current Windows UI culture. An explicit choice is stored for the current user
and applies immediately by rebuilding the tray's localized windows and menu in
the running process. The service and Session Agent continue running throughout
the switch. Number, date, and regional formatting continue to use the Windows
regional culture.

The graphical installer and native setup remain independent processes and
always follow the Windows UI culture. If a culture or individual resource
cannot be resolved, user-facing text falls back to the authoritative en-US
value.

The en-US catalogs are authoritative. The English text left beside an `x:Uid`
in WinUI XAML is a design-time fallback and must match the catalog. The
localization validator checks that copy so the two cannot drift.

## Adding or changing text

For WinUI and graphical installer text, edit the localizable XAML or code
fallback first. The extraction scripts rebuild those en-US catalogs from their
source files, so do not hand-edit the generated catalog value. Give WinUI
elements a stable `x:Uid`; do not use the displayed sentence as the identifier.
Attached properties use their full WinUI resource name. For example:

```text
DashboardRefreshButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name
```

The managed Tray.Core and Setup catalogs use the English sentence as the
resource key and safe runtime fallback. Edit their authoritative en-US entry
alongside the matching `TrayText.Get`, `TrayText.Format`, or `SetupStrings`
call. Add installer text through `InstallerStrings.Get` or
`InstallerStrings.Format`; its extractor copies those fallbacks into the
installer catalog. Keep numbered format placeholders unchanged across
translations.

After editing localizable source text, regenerate the authoritative English
catalogs and pseudo locale in one PowerShell process, then run the validator:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File build\Update-LocalizationResources.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File build\Validate-LocalizationResources.ps1
```

The combined update script runs the two maintainer extraction scripts before
regenerating qps-ploc. They add resource entries for new XAML or installer
literals, but they do not replace review. Check every new key for useful
context and a stable name. The individual extraction and pseudo-localization
scripts remain independently callable when only one stage needs investigation.

## Translation files and fallback

Place WinUI translations in `src/OpenBlade.Tray/Strings/<culture>/Resources.resw`.
Managed installer translations use
`build/installer/OpenBlade.Installer.UI/Properties/Resources.<culture>.resx`.
Tray.Core and native setup translations use the same suffix convention beside
their authoritative `Resources.resx` files.
Do not copy untranslated entries into a real translation. A missing entry falls
back to en-US.

The managed resource wrappers fall back when a satellite resource is absent,
blank, unreadable, or has an invalid format string. A broken translation must
not prevent the tray or setup from starting. Missing or malformed authoritative
resources are build failures because there is no safe translation to fall back
to.

Translations must not change:

- hardware capability claims, warnings, supported model names, or firmware
  requirements;
- enum values, JSON fields, schemas, IPC fields, named pipes, or command-line
  switches;
- registry paths, service and process names, URLs, filenames, extensions, or
  notification tags;
- trace messages, diagnostic keys, protocol values, or sanitized capture
  terminology.

Product names such as OpenBlade, Razer, NVIDIA, and Windows remain unchanged
unless the product owner publishes a localized form.

## Pseudo-localization review

The committed `qps-ploc` catalogs accent, bracket, and expand the en-US text.
Set `OPENBLADE_PSEUDO_LOCALIZATION=1` before starting a development build. The
tray and installer select qps-ploc without changing the user's Windows language.
The isolated tray preview build keeps both the en-US and qps-ploc MRT
qualifiers and uses a distinct executable and PRI identity. Its smoke test
proves that the qps-ploc qualifier supplies compiled `x:Uid` text,
accessibility properties, and strings loaded by code. Production builds keep
en-US as the neutral catalog and do not package qps-ploc.

For an unattended tray review, also set
`OPENBLADE_LOCALIZATION_SNAPSHOT_DIR` to an empty output directory. The tray
starts in a disconnected, read-only snapshot path, captures each primary page
and the tray dashboard, then exits. It does not start the service, Session
Agent, tray icon, or hardware integration. The installer snapshot test uses the
same output variable and renders its states without planning or applying a Burn
package:

Tray visual smoke tests, installer UI tests, and the capture wrapper run their
windows without displaying them on the user's screen. Rendering,
localization, layout, and logical keyboard-focus checks still run. An isolation
failure fails the test; it does not show a window on the user's desktop.
Tray renderers use private Windows desktops. Installer snapshots are cloaked
by Windows before they are shown; tests requiring real keyboard activation use
a private desktop kept for the testhost lifetime. This preserves both screenshot
rendering and real focus checks without affecting the user's desktop. The Tray
harness omits Explorer switcher registration on desktops without a shell.

Run the checked-in capture wrapper so every WPF state gets a fresh testhost and
cannot inherit retained render state from the preceding image:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File build\Capture-LocalizationSnapshots.ps1
```

Pass `-Culture qps-ploc`, `-Culture zh-CN`, or `-Culture ru-RU` to capture one
catalog. Each culture is written to its own directory beneath the requested
output root.

Review these tray surfaces at minimum width and at 100, 150, and 200 percent
text scaling:

- Dashboard, Profiles, Laptop, Lighting, Display, Settings, and About;
- the tray dashboard, each detail view, and its customization view;
- dialogs, InfoBars, picker labels, tooltips, and screen-reader names.

Review every graphical installer state: checking, fresh install, maintenance,
same-version maintenance, older-installer block for a newer installed version,
registration conflict, progress with details hidden and shown, cancellation
confirmation, install and uninstall success, failure, and help.

For each surface, check wrapping, clipping, control overlap, focus visibility,
tab order, keyboard access, and spoken names. Record the exact commit, locale,
DPI, text scale, theme, reviewer, date, and any defect found. The executable
smoke tests prove that the resource-backed XAML loads; they do not replace this
visual review.

## Review expectations

A translation review checks language and technical meaning separately. A
reviewer familiar with the target language checks tone, grammar, placeholders,
and access keys. An OpenBlade maintainer checks hardware facts, safety wording,
and capability semantics against en-US. Both reviews are required before a new
culture is added to a release payload.

### Simplified Chinese style

Write `zh-CN` text as concise, neutral Windows UI copy instead of preserving
English sentence structure. Prefer direct state descriptions and actions, omit
subjects when Chinese does not need them, and use `请` only when the user must
take an action. Do not remove conditions, consequences, or distinctions between
checking, verification, submission, completion, application, and confirmed
readback merely to shorten a sentence.

Use these terms consistently:

- `配置方案` for OpenBlade power-source profiles, with `方案` permitted only in
  compact labels where the referent is unambiguous;
- `实时数据` for live readings or details;
- `控制项` for interactive UI controls and plain `控制` for a capability;
- `已在 OpenBlade 外部更改` for state changed outside OpenBlade;
- `安装程序` for the running installer, `安装包` for a bundle or build,
  `安装项` for a registered Windows Installed apps entry, and `注册项` only
  when the registration mechanism itself matters;
- `卸载` for removing OpenBlade, `移除` for registered entries or components,
  and `删除` for files or saved data;
- `偏好设置` for preferences and `设置` for settings; and
- `恢复之前的安装状态` for installer rollback.

Keep `OpenBlade`, `Razer`, `NVIDIA`, `Windows`, and the exact `Session Agent`
component name unchanged. Use `显示助手` only when the English source describes
the helper role rather than naming that component. Translate `checking` as
`检查`; use `验证通过` or `已确认` only when the authoritative state actually
establishes that stronger result.
