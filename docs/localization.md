# Localization

OpenBlade keeps its source language in three resource catalogs:

- `src/OpenBlade.Tray/Strings/en-US/Resources.resw` contains WinUI text,
  tooltips, and accessibility names.
- `build/installer/OpenBlade.Installer.UI/Properties/Resources.resx` contains
  the graphical installer text.
- `src/OpenBlade.Tray.Core` remains independent of WinUI. Protocol values,
  persisted values, diagnostics, and logs stay language neutral.

The en-US catalogs are authoritative. The English text left beside an `x:Uid`
in WinUI XAML is a design-time fallback and must match the catalog. The
localization validator checks that copy so the two cannot drift.

## Adding or changing text

Edit the en-US resource first. Give WinUI elements a stable `x:Uid`; do not use
the displayed sentence as the identifier. Attached properties use their full
WinUI resource name. For example:

```text
DashboardRefreshButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name
```

The installer uses the English sentence as its resource key and as its safe
runtime fallback. Add imperative installer text through `InstallerStrings.Get`
or `InstallerStrings.Format`. Keep numbered format placeholders unchanged
across translations.

After editing the source catalogs, regenerate the pseudo locale and run the
validator:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File build\Update-PseudoLocalization.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File build\Validate-LocalizationResources.ps1
```

The extraction scripts are for maintainers. They add resource entries for new
XAML or installer literals, but they do not replace review. Check every new key
for useful context and a stable name.

## Translation files and fallback

Place WinUI translations in `src/OpenBlade.Tray/Strings/<culture>/Resources.resw`.
Managed installer translations use
`build/installer/OpenBlade.Installer.UI/Properties/Resources.<culture>.resx`.
Do not copy untranslated entries into a real translation. A missing entry falls
back to en-US.

The graphical installer also falls back when a satellite resource is absent,
blank, unreadable, or has an invalid format string. A broken translation must
not prevent setup from starting. Missing or malformed authoritative resources
are build failures because there is no safe translation to fall back to.

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

Review these tray surfaces at minimum width and at 100, 150, and 200 percent
text scaling:

- Dashboard, Profiles, Laptop, Lighting, Display, Settings, and About;
- the tray dashboard, each detail view, and its customization view;
- dialogs, InfoBars, picker labels, tooltips, and screen-reader names.

Review every graphical installer state: checking, fresh install, maintenance,
same-version maintenance, newer-version replacement, registration conflict,
progress with details hidden and shown, cancellation confirmation, install and
uninstall success, failure, and help.

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
