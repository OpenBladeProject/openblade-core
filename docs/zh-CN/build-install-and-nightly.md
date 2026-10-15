<a id="build-installation-and-nightly-releases"></a>

# 构建、安装与 Nightly 发布

[English](../build-install-and-nightly.md) | 简体中文 | [Русский](../ru-RU/build-install-and-nightly.md)

本文说明正式发布包的构建、安装与恢复方法，以及 Nightly 发布流程。
普通源码构建请使用[开发者指南](../developer-guide.md#build-and-test)中的命令。

<a id="publish-artifacts"></a>

## 发布构建产物

创建图形安装程序、`win-x64` 生产包，以及独立的开发者工具：

```powershell
.\build\Publish.ps1
```

- 图形安装程序：`artifacts\win-x64\OpenBlade-Setup.exe`
- 解压后的正式发布包和命令行安装工具：`artifacts\win-x64\OpenBlade`
- 开发者工具：由同一脚本单独发布

已安装的应用使用系统级 x64 .NET 10 Desktop Runtime。
如果缺少兼容运行时，图形安装程序会在替换 OpenBlade 前询问是否下载并安装。
Visual C++ 和 Windows App SDK 仍随安装包提供。
安装工具和开发者工具自带所需的运行时。

共享运行时必须是稳定的 10.0 补丁版本，且不低于发布的最低版本。
安装工具会像 .NET apphost 一样，从 32 位注册表视图中读取 x64 运行时的安装位置。
准备更新文件前，它会检查所选的 Desktop Runtime，以及该版本声明依赖的核心运行时。
OpenBlade 修复操作不会改动已安装的共享运行时。
如果选中的共享运行时损坏，请先用 Microsoft 的 .NET 10 Desktop Runtime
安装程序修复该版本，再重试 OpenBlade 安装。

如需离线安装程序，请运行 `build/Publish.ps1 -OfflineInstaller`。
这样会把同一份经过验证的 .NET 必备组件打包进去。面向 Store 的安装包应使用此选项。

开发阶段可不签名。公开发布签名构建时，请向 `Publish.ps1` 提供证书指纹。
脚本会先为 OpenBlade 自身的文件签名，再交给 WiX 打包，
然后依次为分离的 Burn 引擎和最终安装程序签名。

<a id="interactive-installation"></a>

## 交互式安装

- 双击 `OpenBlade-Setup.exe` 并选择安装。
  安装程序准备修改系统时，Windows 才会请求管理员批准。
- 安装程序采用 Windows 的应用主题。在安装、修复或卸载过程中选择**显示详细信息**，
  可查看正在更改的文件、注册表项、快捷方式和服务。
- 安装或修复成功后，选择**启动 OpenBlade**。
  托盘程序会启动 Session Agent，两者均在普通用户会话中运行。
- 不要使用**以管理员身份运行**启动安装程序，否则托盘程序和 Session Agent 也会以管理员权限运行。
  如果已经这样启动，请关闭它们，再正常启动 OpenBlade。

<a id="quiet-installation"></a>

## 静默安装

- 静默安装不显示安装界面，也不启动应用。
- `/quiet` 成功返回后，调用安装程序的自动化脚本必须通过原来的普通权限进程启动
  已安装的 `OpenBlade.Tray.exe`，并确认托盘程序和 Session Agent 正在运行。
- Microsoft Store EXE 渠道不支持这种由调用方重新启动应用的方式，
  因为静默调用由 Partner Center 管理。剩余问题见
  [议题 #25](https://github.com/OSSBlade/openblade-core/issues/25)。

<a id="versioned-development-builds"></a>

## 开发构建的版本号

如果需要安装多个开发构建，又不希望更改基础产品版本，请让每次构建的 Nightly 修订号依次递增：

```powershell
.\build\Publish.ps1 -BuildChannel Nightly -BuildRevision 42 -SourceRevision (git rev-parse HEAD)
```

- 图形安装程序检测到缓存中只有一个同版本安装包时，会提供**修复**和**卸载**。
  修复会用当前安装包中的文件覆盖已安装文件；卸载会调用已安装版本的卸载流程，并保留已保存的配置。
  修复还会持久保存每个安装包的所有权令牌，防止旧安装包删除新安装的文件，即使两个包的修订号相同。
  如需同时删除已保存的配置，请关闭当前安装程序，
  从 Windows 的“已安装的应用”中卸载已登记的 OpenBlade，
  并在那里选择**删除已保存的配置和设置**。
- 静默 `/install` 仍将相同版本视为版本冲突，因为它无法代替用户选择维护操作。
  无人值守维护请明确使用 `/repair` 或 `/uninstall`。
- 每次验证新的构建时，都应使用更高的 Nightly 修订号。
  不要安装产品版本相同但内容不同的 Stable 产物。

<a id="duplicate-bundle-registrations"></a>

## 重复的安装记录

如果发现多个相关的 OpenBlade 安装记录，安装程序会在 Apply 阶段前停止。
旧安装程序可能在 Burn 卸载过程中反复互相调用，造成死锁。

- 同时存在一个旧版本和一个新版本的安装记录时，也会触发这项保护，因为无法确定应该替换哪个安装包。
- 交互式安装在检测后报告冲突，不提供安装或修复选项。
- 如果安装程序为其自身已登记的安装包提供卸载选项，请先使用该选项。
- 重试新版安装程序前，请使用 Windows 的安装恢复功能清除剩余的重复记录。

<a id="command-line-setup-and-recovery"></a>

## 命令行安装与恢复

解包的生产包仍位于 `artifacts\win-x64\OpenBlade`，供开发和恢复使用。
需要时，在管理员终端中运行其中的安装工具。
独立安装要求系统级 .NET Desktop Runtime 已安装；可用图形安装程序获取：

```powershell
.\artifacts\win-x64\OpenBlade\OpenBlade.Setup.exe install
```

可通过 Windows 设置卸载，或重新运行图形安装程序。
两者均保留已保存的配置和偏好设置。
独立安装工具还支持 `uninstall --purge`，在拥有安装所有权时删除已保存数据。

如果图形安装程序已丢失但其所有权标记仍在，请使用：

```powershell
.\artifacts\win-x64\OpenBlade\OpenBlade.Setup.exe uninstall --recover-orphaned-bundle
```

<a id="microsoft-store-channel"></a>

## Microsoft Store 渠道

- 未来的 Microsoft Store EXE 上架版本使用上述离线构建。
- Store 安装使用 `/install /quiet /norestart`；直接下载仍保留图形界面。
- Store 无法在静默安装后按要求以普通用户权限重新启动应用，
  因此在[议题 #25](https://github.com/OSSBlade/openblade-core/issues/25)解决前，
  安装后启动应用这一环节仍未解决。

安装包要求、认证说明、硬件声明和后续发布步骤，
请参阅 [Microsoft Store 提交](../microsoft-store-submission.md)。

<a id="nightly-workflow"></a>

## Nightly 工作流

- `Nightly` GitHub Actions 工作流仅在维护者手动启动时构建最新提交的 `main`，
  没有定时调度。
- 它会执行与 CI 相同的格式、构建、测试、安装包定义和正式发布检查，
  生成的 Actions 构建产物保留 14 天。
- 每次构建成功后，工作流会创建一个 GitHub 预发布草稿，其中包含安装程序、校验和、
  元数据，以及与本次构建提交对应的更新日志快照。
- 工作流还会创建或更新文档 PR 草稿，补齐尚未记录的 Nightly 发布分界，
  并保留 `main` 上较新的 `Unreleased` 条目。
- 审核并合并更新日志 PR，然后在 `main` 上运行 **Finalize Nightly**，
  填入该草稿对应的确切标签。工作流会先验证构建产物和已合并的更新日志，再正式发布。
- 已公开的 Nightly 显示在 Releases 页面。
  只有选择 **Nightly** 更新渠道时，应用才会提供这些版本。

Nightly 是测试构建。安装前，请用附带的 SHA-256 文件验证 `OpenBlade-Setup.exe`。
如想帮助测试笔记本，请按[Blade 实验性支持指南](experimental-blade-support.md)
了解可用功能和测试报告要求。无论选择哪个更新渠道，OpenBlade 都会自动启用已识别型号的可用功能。

<a id="attestations-and-signing-status"></a>

### 构建证明与签名状态

GitHub 构建来源证明属于可选功能，能否使用取决于仓库是公开还是私有，以及所用的计费方案。
确认支持后，维护者可将 `OPENBLADE_ENABLE_ATTESTATIONS` 设为 `true`，
并用以下命令验证已启用的证明：

```powershell
gh attestation verify .\OpenBlade-Setup.exe --repo OSSBlade/openblade-core
```

随包附带的 JSON 记录了构建提交、递增的 Nightly 版本号、构建时间、工作流运行记录、
安装程序哈希和 Authenticode 签名状态。
如果工作流没有配置受保护的发布签名证书，安装程序的签名状态应为 `NotSigned`，
Windows 可能弹出无法识别发布者的警告。

<a id="changelog-publication"></a>

### 更新日志发布

- 更新日志任务要求仓库启用 **Allow GitHub Actions to create and approve pull requests**。
- 该任务中的令牌权限仅限仓库内容和拉取请求。
- 如果该设置已禁用，请启用后重跑失败的更新日志任务。
  在更新日志合并并通过验证前，版本会保持草稿状态。
- 如果更新日志生成或附件处理出错，准备流程就会停止。
  如果来源快照、已合并章节、比较链接或产物不匹配，最终发布也会停止。
  请修正所报告的问题，并对同一草稿标签重新执行最终发布。
- 托管和本地发布都使用 `build/Complete-NightlyRelease.ps1`。
  可使用 `-VerifyOnly` 检查已准备的草稿而不公开发布。
