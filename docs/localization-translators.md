# Translating OpenBlade

You do not need Visual Studio or programming experience to translate OpenBlade.
Maintainers export one ZIP file containing an Excel-compatible CSV and source-language
screenshots. You edit one column and return the CSV for automated validation.

## For translators

1. Extract the ZIP file and open `OpenBlade-<locale>.csv` in Microsoft Excel or
   LibreOffice Calc. Excel recognizes the file as UTF-8 because it includes a UTF-8
   byte-order mark.
2. Edit only the **Translation** column. Do not rename columns, change keys, delete
   rows, or edit source/context/hash columns.
3. Use **Context** and **Screenshots** to see where the text appears. Screenshot paths
   are relative to the extracted package folder. **Constraints** lists anything that
   must remain exact.
4. Keep items such as `{0}`, `{1:N0}`, markup tags, model identifiers, URLs, and file
   names unchanged. Keep intentional leading/trailing spaces and line breaks. For
   menu shortcuts, keep the stated number of underscore markers, but put each marker
   before a suitable letter in your language.
5. Fill every Translation cell. If a translation must literally begin with `=`, `+`,
   `-`, or `@`, prefix it with one Tab character. This marks it as text instead of an
   Excel formula; validation removes that one transport-only Tab.
6. Save the file as **CSV UTF-8 (Comma delimited)**. Return the CSV, not a converted
   `.xlsx` file. Maintainers validate it before opening or importing it.

Automated feedback names the surface and key that needs attention. Common messages
mean:

- **stale or tampered**: the English source, current target catalogs, or package
  structure changed; ask for a fresh export and copy translations into its
  Translation column.
- **changed format placeholders**: restore every complete item such as `{0}` or
  `{1:N0}`. The items may move to fit your grammar.
- **changed markup/access-key/whitespace**: restore the markup structure, underscore
  shortcut count, distinct shortcut letters for actions shown together, or intentional
  spaces and line breaks shown in Constraints.
- **formula character**: prefix a legitimate leading `=`, `+`, `-`, or `@` with one
  Tab, or remove it if it was accidental.

## For maintainers

Restore the ordinary solution, the preview build, and the disconnected installer
snapshot test once in a fresh worktree:

```powershell
dotnet restore OpenBlade.slnx -p:NuGetAudit=false
dotnet restore .\src\OpenBlade.Tray\OpenBlade.Tray.csproj `
    -p:NuGetAudit=false -p:OpenBladeLocalizationPreview=true
dotnet restore `
    .\build\installer\OpenBlade.Installer.UI.Tests\OpenBlade.Installer.UI.Tests.csproj `
    -p:NuGetAudit=false
```

Export a package from the repository root:

```powershell
.\build\Export-LocalizationTranslatorPackage.ps1 -Locale ru-RU
```

The default command renders fixed en-US context through the disconnected localization
preview, writes a folder under `artifacts/localization/translator/`, and creates a ZIP
with sorted entries and fixed timestamps. Child renderers use a process-local 96-DPI
compatibility context, a fixed light theme, software installer rendering, and sanitized
sample data. Captured pixels use a fixed review-safe color palette so harmless renderer
antialias variation cannot change package bytes. Snapshot mode omits commit build metadata
from the displayed app version,
so an incremental build cannot leak a superseded head into an otherwise current package.
That render profile and every image hash are recorded in the package manifest; system
display settings are not changed. It does not install OpenBlade,
request UAC, start the service or Session Agent, or perform hardware writes. The output
directory is gitignored; do not commit generated CSV, ZIP, or PNG files.

Validate a returned workbook before opening it:

```powershell
.\build\Import-LocalizationTranslatorWorkbook.ps1 `
    -Workbook C:\path\to\OpenBlade-ru-RU.csv `
    -ValidateOnly
```

Import only after validation succeeds:

```powershell
.\build\Import-LocalizationTranslatorWorkbook.ps1 `
    -Workbook C:\path\to\OpenBlade-ru-RU.csv
```

The importer reconstructs the current source model from all four authoritative
catalogs. It accepts reordered rows, but rejects missing, extra, duplicate,
structurally incomplete, stale, or altered rows before writing. A workbook is also
bound to the exact target-catalog files that existed at export, so a newer translation
cannot be silently overwritten. Accepted imports rebuild each locale from the current
authoritative source keyset, add newly translated keys, and remove obsolete keys. The
four source catalogs remain read-locked from model reconstruction through validation
and writing, preventing a concurrent source edit from bypassing the workbook hashes.
Before checking the target hash, the importer also opens all four existing target
catalogs with write-denying handles (and reserves absent files with create-new
semantics), then retains those handles through staged writes, validation, and rollback.
This makes a concurrent editor save fail instead of silently losing either version.
The importer validates complete .NET composite-format items, markup, access-key
counts, and collisions among simultaneously visible installer actions, significant
whitespace, line breaks,
punctuation-only values, protected technical identifiers, control and unsafe Unicode
format characters, and spreadsheet-formula prefixes. If writing or repository
localization validation fails,
all touched catalogs are restored. Import never edits language admission or packaging;
export and import accept only the currently admitted `zh-CN` and `ru-RU` production
locales. Adding another production locale remains a separate reviewed change.
`qps-ploc` is always rejected as a translator target and remains development-only.

The workbook's stable identity is `Surface + Key`. Format version 2 gives each row a
SHA-256 hash over its immutable source/context fields, repeats a source catalog hash
over all ordered row hashes, and repeats a target catalog hash over the exact four
locale files present at export. These are deterministic change detectors, not
signatures; the importer's fresh comparison with the checked-out authoritative
catalogs is the trust boundary.

## Why this format

OpenBlade uses a UTF-8 BOM, RFC 4180-style CSV inside a deterministic ZIP instead of
XLSX or XLIFF. CSV opens in ordinary spreadsheet software, stays inspectable as text,
needs no macro-capable binary workbook, and maps cleanly to a Git-reviewed import.
XLIFF remains the richer industry interchange standard, but its XML/tooling workflow is
less approachable for the nontechnical translators in issue #274.

The design follows these current sources and comparable open-source workflows:

- [Microsoft: opening UTF-8 CSV files correctly in Excel](https://support.microsoft.com/en-us/office/opening-csv-utf-8-files-correctly-in-excel-8a935af5-3416-4edd-ba7e-3dfd2bc4a032)
- [Microsoft: contextual metadata for localization](https://learn.microsoft.com/en-us/globalization/internationalization/contextual-metadata)
- [Microsoft: .NET composite formatting](https://learn.microsoft.com/en-us/dotnet/standard/base-types/composite-formatting)
- [Weblate: CSV files](https://docs.weblate.org/en/latest/formats/csv.html),
  [translation checks](https://docs.weblate.org/en/latest/user/checks.html), and
  [visual context screenshots](https://docs.weblate.org/en/latest/admin/translating.html)
- [OASIS XLIFF 2.1](https://docs.oasis-open.org/xliff/xliff-core/v2.1/xliff-core-v2.1.html)
- [GNU gettext: PO entries](https://www.gnu.org/software/gettext/manual/html_node/PO-File-Entries.html)
  and [message contexts](https://www.gnu.org/software/gettext/manual/html_node/Contexts.html)
- [NIST FIPS 180-4 secure hash standard](https://csrc.nist.gov/pubs/fips/180-4/upd1/final)
- [OWASP: CSV injection](https://owasp.org/www-community/attacks/CSV_Injection)
- [Mozilla Pontoon](https://github.com/mozilla/pontoon), a comparable repository-backed
  open-source workflow with a nontechnical translation interface.

The deliberate tradeoff from gettext/Weblate is stricter staleness handling: OpenBlade
rejects an outdated workbook rather than importing it as fuzzy work because four
separately packaged resource surfaces must remain complete and synchronized.
