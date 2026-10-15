<a id="translating-openblade"></a>

# 翻译 OpenBlade

[English](../localization-translators.md) | 简体中文 | [Русский](../ru-RU/localization-translators.md)

翻译 OpenBlade 不需要 Visual Studio 或编程经验。
维护者会导出一个 ZIP，包含 Excel 兼容的 CSV 和源语言截图。
你只需编辑一列，再交回 CSV，由自动化进行验证。

<a id="for-translators"></a>

## 给翻译者

1. 解压 ZIP，用 Microsoft Excel 或 LibreOffice Calc 打开 `OpenBlade-<locale>.csv`。
   文件带有 UTF-8 字节顺序标记，因此 Excel 会将其识别为 UTF-8。
2. 只编辑 **Translation** 列。不要重命名列、修改键、删除行，
   或编辑源文、上下文及哈希列。
3. 使用 **Context** 和 **Screenshots** 了解文字出现的位置。
   截图路径相对于解压后的包目录。**Constraints** 列列出必须原样保留的内容。
4. 保持 `{0}`、`{1:N0}`、标记标签、型号标识、URL 和文件名等项目不变。
   保留有意设置的首尾空格和换行。菜单快捷键需保留指定数量的下划线标记，
   但可将每个标记放到适合目标语言的字母前。
5. 填写每个 Translation 单元格。如果译文确实需要以 `=`、`+`、`-` 或 `@` 开头，
   请在前面加一个 Tab 字符。这会将其标记为文本，避免成为 Excel 公式；
   验证时会移除这个仅用于传输的 Tab。
6. 保存为 **CSV UTF-8 (Comma delimited)**。请交回 CSV，而非转换后的 `.xlsx`。
   维护者会在打开或导入前验证文件。

自动反馈会指出需要处理的资源面和键。常见消息含义如下：

- **stale or tampered**：英文源文、当前目标资源目录或包结构已变化；
  请索取新导出包，再把译文复制到其中的 Translation 列。
- **changed format placeholders**：恢复 `{0}` 或 `{1:N0}` 等每个完整项目。
  可根据语法调整它们的位置。
- **changed markup/access-key/whitespace**：恢复标记结构、下划线快捷键数量、
  同时显示的操作之间互不重复的快捷键字母，或 Constraints 中显示的有意空格与换行。
- **formula character**：在确实需要的前导 `=`、`+`、`-` 或 `@` 前加一个 Tab；
  如果它是不小心输入的，则将其删除。

<a id="for-maintainers"></a>

## 给维护者

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

默认命令使用离线本地化预览渲染固定的 en-US 上下文，
在 `artifacts/localization/translator/` 下写入目录，
并创建条目排序、时间戳固定的 ZIP。
子渲染器使用进程本地 96-DPI 兼容上下文、固定浅色主题、
安装程序软件渲染和已脱敏示例数据。
采集像素使用固定的审核安全调色板，防止无害的抗锯齿差异改变包字节。
快照模式会从显示的应用版本中省略提交构建元数据，
因此增量构建不会把过时提交泄露进当前包。
渲染配置和每张图像的哈希都会记录在包清单中；不会改变系统显示设置。
它不会安装 OpenBlade、请求 UAC、启动服务或 Session Agent，也不会执行硬件写入。
输出目录已被 gitignore 排除；不要提交生成的 CSV、ZIP 或 PNG。

任一离线渲染器达到现有的十秒画面稳定超时时，
`Capture-LocalizationSnapshots.ps1` 会将最后两个规范化画面保留在
`artifacts/translator-archive-failures/snapshot-stability/<culture>/<capture-id>/`
下，以资源面/阶段和唯一失败 ID 区分。每个画面上限为 32 MiB。
完整的失败证据包含 `previous.bgra`、`candidate.bgra`，
以及记录各画面尺寸和 SHA-256 的 `metadata.json`。
`metadata.json` 是完成标记，缺少它的目录仅包含保留失败时的部分证据。
此保留机制适用于调用该脚本的快照采集和翻译导出工作流。
直接启动本地化测试工具不属于配置的工作流，
性能行的 `--capture` 输出也明确不属于本地化采集/导出范围。
成功渲染不会创建失败证据目录。

打开返回的工作簿前，先验证：

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

导入器会从四个权威资源目录重建当前源模型。
它接受重新排序的行，但会在写入前拒绝缺失、额外、重复、结构不完整、
过时或被修改的行。工作簿还绑定导出时存在的精确目标资源文件，
因此不会静默覆盖较新的翻译。
接受的导入会按当前权威源键集重建各语言资源，加入新翻译的键并移除过时键。
从模型重建、验证直到写入，四个源资源目录始终保持读取锁定，
避免并发源文编辑绕过工作簿哈希。
在检查目标哈希前，导入器还会以禁止写入的句柄打开四个现有目标资源目录文件
（对不存在的文件使用仅新建语义预留），
并在暂存写入、验证和回滚期间保持这些句柄。
这样并发编辑器保存会失败，而不会静默丢失任一版本。
导入器验证完整的 .NET 复合格式项目、标记、访问键数量、
同时可见的安装程序操作之间的访问键冲突、有意义的空白、换行、
仅标点值、受保护技术标识、控制字符、不安全的 Unicode 格式字符，
以及电子表格公式前缀。
如果写入或仓库本地化验证失败，所有修改过的资源目录都会恢复。
导入不会编辑语言准入或打包配置；导出和导入仅接受当前已准入的
`zh-CN` 与 `ru-RU` 生产语言。
添加其他生产语言仍需单独审核。
`qps-ploc` 始终被拒绝作为翻译目标，仅用于开发。

工作簿的稳定身份为 `Surface + Key`。
格式版本 2 为每行不可变的源文/上下文字段计算 SHA-256，
在每行重复所有有序行哈希的源目录哈希，
并重复导出时四个精确语言文件的目标目录哈希。
这些是确定性的变更检测器，不是签名；
导入器对当前检出权威目录的新鲜比较才是信任边界。

<a id="why-this-format"></a>

## 为什么使用此格式

OpenBlade 使用带 UTF-8 BOM、符合 RFC 4180 风格的 CSV，
并将其放入确定性 ZIP，而非采用 XLSX 或 XLIFF。
CSV 可用普通电子表格软件打开，也便于直接检查文本，
无需支持宏的二进制工作簿，并能直接对应经过 Git 审核的导入。
XLIFF 仍是功能更丰富的行业交换标准，
但其 XML/工具流程对议题 #274 中的非技术翻译者较难上手。

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
由于四个独立打包的资源面必须保持完整和同步，
过时工作簿会被拒绝，不会作为模糊匹配结果导入。
