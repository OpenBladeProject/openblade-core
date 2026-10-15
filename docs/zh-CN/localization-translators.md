<a id="translating-openblade"></a>

# 翻译 OpenBlade

[English](../localization-translators.md) | 简体中文 | [Русский](../ru-RU/localization-translators.md)

翻译 OpenBlade 不需要 Visual Studio 或编程经验。
维护者会提供一个 ZIP 压缩包，内含可用 Excel 打开的 CSV 文件和英文界面截图。
你只需填写其中的译文列，再交回 CSV 文件进行自动检查。

<a id="for-translators"></a>

## 翻译步骤

1. 解压 ZIP，用 Microsoft Excel 或 LibreOffice Calc 打开 `OpenBlade-<locale>.csv`。
   文件带有 UTF-8 字节顺序标记，因此 Excel 会将其识别为 UTF-8。
2. 只编辑 **Translation** 列。不要重命名列、修改键、删除行，
   或编辑源文、上下文及哈希列。
3. 使用 **Context** 和 **Screenshots** 了解文字出现的位置。
   截图路径相对于解压后的包目录。**Constraints** 列列出必须原样保留的内容。
4. 保持 `{0}`、`{1:N0}`、标记标签、型号标识、URL 和文件名等项目不变。
   原文要求的首尾空格和换行也要保留。菜单快捷键的下划线标记数量必须不变，
   但可将每个标记放到适合目标语言的字母前。
5. 填写每个 Translation 单元格。如果译文确实需要以 `=`、`+`、`-` 或 `@` 开头，
   请在前面加一个 Tab 字符。这会将其标记为文本，避免成为 Excel 公式；
   检查文件时会移除这个用于标记文本的 Tab。
6. 保存为 **CSV UTF-8 (Comma delimited)**。请交回 CSV，而非转换后的 `.xlsx`。
   维护者会在打开或导入前验证文件。

检查结果会指出哪一类资源（Surface）中的哪个键需要修改。常见提示如下：

- **stale or tampered**：英文原文、当前语言的资源文件或压缩包结构发生了变化；
  请索取新导出包，再把译文复制到其中的 Translation 列。
- **changed format placeholders**：将 `{0}` 或 `{1:N0}` 等占位符恢复完整。
  可根据语法调整它们的位置。
- **changed markup/access-key/whitespace**：恢复标记结构、下划线快捷键数量、
  确保同时显示的操作使用不同的快捷键字母，并保留 Constraints 中要求的空格与换行。
- **formula character**：在确实需要的前导 `=`、`+`、`-` 或 `@` 前加一个 Tab；
  如果它是不小心输入的，则将其删除。

<a id="for-maintainers"></a>

## 维护者操作

在新工作树中，先为常规解决方案、预览构建和离线安装程序快照测试各恢复一次依赖：

```powershell
dotnet restore OpenBlade.slnx -p:NuGetAudit=false
dotnet restore .\src\OpenBlade.Tray\OpenBlade.Tray.csproj `
    -p:NuGetAudit=false -p:OpenBladeLocalizationPreview=true
dotnet restore `
    .\build\installer\OpenBlade.Installer.UI.Tests\OpenBlade.Installer.UI.Tests.csproj `
    -p:NuGetAudit=false
```

从仓库根目录导出包：

```powershell
.\build\Export-LocalizationTranslatorPackage.ps1 -Locale ru-RU
```

默认命令通过离线本地化预览生成固定的 en-US 界面截图，
将文件保存到 `artifacts/localization/translator/` 下的文件夹，
并打包成文件顺序和时间戳固定的 ZIP。
负责渲染的子进程使用仅对自身生效的 96-DPI 兼容设置、固定浅色主题
和已脱敏的示例数据；安装程序截图采用软件渲染。
截图像素会转换到一套适合审核的固定调色板，避免细微的抗锯齿差异导致压缩包内容变化。
截图中的应用版本不显示提交编号等构建元数据，
以免增量构建将旧提交的信息带入当前压缩包。
渲染设置和每张图片的哈希都会记入包清单，系统显示设置不会改变。
它不会安装 OpenBlade、请求 UAC、启动服务或 Session Agent，也不会执行硬件写入。
输出目录已被 gitignore 排除；不要提交生成的 CSV、ZIP 或 PNG。

任一离线渲染器等待十秒后画面仍未稳定时，
`Capture-LocalizationSnapshots.ps1` 会将最后两个规范化画面保留在
`artifacts/translator-archive-failures/snapshot-stability/<culture>/<capture-id>/`
下，按资源类型、处理阶段和唯一失败 ID 分别存放。每个画面最多占用 32 MiB。
完整的失败证据包含 `previous.bgra`、`candidate.bgra`，
以及记录各画面尺寸和 SHA-256 的 `metadata.json`。
`metadata.json` 表示证据已保存完整；缺少这个文件，说明保存过程失败，目录中只有部分证据。
调用该脚本的截图采集和翻译导出流程都会保留这些证据。
直接启动本地化测试工具时不适用，
性能行的 `--capture` 输出也明确不属于本地化采集/导出范围。
成功渲染不会创建失败证据目录。

收到译者交回的工作簿后，先检查，再打开：

```powershell
.\build\Import-LocalizationTranslatorWorkbook.ps1 `
    -Workbook C:\path\to\OpenBlade-ru-RU.csv `
    -ValidateOnly
```

仅在验证成功后导入：

```powershell
.\build\Import-LocalizationTranslatorWorkbook.ps1 `
    -Workbook C:\path\to\OpenBlade-ru-RU.csv
```

导入器先读取四个作为唯一依据的源资源文件，重建当前源文模型。
工作簿可以调整行的顺序，但如果存在缺行、多余行、重复行、结构不完整、
内容过时或不允许的修改，导入器会在写入前拒绝导入。
它还会核对导出时的目标语言资源文件，避免覆盖后来更新的译文。
检查通过后，导入器按当前源文中的键重建各语言资源，加入新译文并删除已废弃的键。
从重建模型到检查和写入完成，四个源资源文件始终保持读锁，禁止其他进程修改，
确保工作簿哈希检查不会因同时编辑源文而失效。
检查目标哈希前，导入器也会打开四个现有目标语言资源文件，并禁止其他进程写入。
对于尚不存在的文件，则以“仅允许新建”的方式创建并占用。
这些文件句柄会一直保留到暂存写入、检查和回滚结束。
此时其他编辑器若尝试保存，会收到失败结果，双方的修改都不会被无声覆盖。
导入器验证完整的 .NET 复合格式项目、标记、访问键数量、
同时可见的安装程序操作之间的访问键冲突、有意义的空白、换行、
仅含标点的值、需要原样保留的技术标识、控制字符、不安全的 Unicode 格式字符，
以及电子表格公式前缀。
如果写入或仓库的本地化检查失败，所有已修改的资源文件都会恢复。
导入不会改变应用支持的语言或打包配置。导出和导入目前只接受正式支持的
`zh-CN` 与 `ru-RU`。
要增加其他正式支持的语言，仍需单独提交审核。
`qps-ploc` 始终被拒绝作为翻译目标，仅用于开发。

工作簿用 `Surface + Key` 唯一标识每条资源。
格式版本 2 会对每行不可修改的源文和上下文字段计算 SHA-256。
所有行哈希按固定顺序汇总后，生成源资源哈希；导出时的四个目标语言文件则生成目标资源哈希。
这两个汇总哈希会写入每一行。
哈希用于检测内容变化，不是数字签名。导入是否可信，
取决于导入器是否重新读取当前检出版本中作为依据的资源文件并与工作簿核对。

<a id="why-this-format"></a>

## 为什么使用此格式

OpenBlade 使用带 UTF-8 BOM、符合 RFC 4180 风格的 CSV，
并将其放入相同输入始终生成相同内容的 ZIP，而非采用 XLSX 或 XLIFF。
CSV 可用普通电子表格软件打开，也便于直接检查文本，
无需使用支持宏的二进制工作簿；导入后的变更也便于在 Git 中审核。
XLIFF 仍是功能更丰富的行业交换标准，
但对于议题 #274 中没有技术背景的译者，XML 和配套工具的使用门槛较高。

设计参考以下现有来源和类似开源工作流：

- [Microsoft：在 Excel 中正确打开 UTF-8 CSV](https://support.microsoft.com/en-us/office/opening-csv-utf-8-files-correctly-in-excel-8a935af5-3416-4edd-ba7e-3dfd2bc4a032)
- [Microsoft：本地化上下文元数据](https://learn.microsoft.com/en-us/globalization/internationalization/contextual-metadata)
- [Microsoft：.NET 复合格式](https://learn.microsoft.com/en-us/dotnet/standard/base-types/composite-formatting)
- [Weblate：CSV 文件](https://docs.weblate.org/en/latest/formats/csv.html)、
  [翻译检查](https://docs.weblate.org/en/latest/user/checks.html)和
  [可视上下文截图](https://docs.weblate.org/en/latest/admin/translating.html)
- [OASIS XLIFF 2.1](https://docs.oasis-open.org/xliff/xliff-core/v2.1/xliff-core-v2.1.html)
- [GNU gettext：PO 条目](https://www.gnu.org/software/gettext/manual/html_node/PO-File-Entries.html)
  和[消息上下文](https://www.gnu.org/software/gettext/manual/html_node/Contexts.html)
- [NIST FIPS 180-4 安全哈希标准](https://csrc.nist.gov/pubs/fips/180-4/upd1/final)
- [OWASP：CSV 注入](https://owasp.org/www-community/attacks/CSV_Injection)
- [Mozilla Pontoon](https://github.com/mozilla/pontoon)：采用仓库工作流，
  并提供非技术翻译界面的类似开源项目。

OpenBlade 对过时内容的处理比 gettext/Weblate 更严格。
四类资源会分别打包，必须保持完整、同步，
因此过时的工作簿会被拒绝导入，不会按模糊匹配处理。
