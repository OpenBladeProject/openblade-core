<a id="build-installation-and-nightly-releases"></a>

# 构建、安装与 Nightly 发布

[English](../build-install-and-nightly.md) | 简体中文 | [Русский](../ru-RU/build-install-and-nightly.md)

本文介绍生产发布、安装、恢复和 Nightly 发布流程。
普通源码构建请使用[开发者指南](../developer-guide.md#build-and-test)中的命令。

<a id="publish-artifacts"></a>

## 发布构建产物

创建图形安装程序、`win-x64` 生产包，以及独立的开发者工具：

```powershell
.\build\Publish.ps1
```

- 图形安装程序：`artifacts\win-x64\OpenBlade-Setup.exe`
- 解包的生产包和命令行安装工具：`artifacts\win-x64\OpenBlade`
- 开发者工具：由同一脚本单独发布

已安装的应用使用系统级 x64 .NET 10 Desktop Runtime。
如果缺少兼容运行时，图形安装程序会在替换 OpenBlade 前询问是否下载并安装。
Visual C++ 和 Windows App SDK 仍随包提供。
安装工具和开发者工具仍采用自包含方式。

共享运行时必须是稳定的 10.0 补丁版本，且不低于发布的最低版本。
安装工具与 .NET apphost 一样，使用 32 位注册表视图中登记的 x64 位置。
在暂存更新前，它会检查选中的 Desktop Runtime 及其声明的核心运行时依赖。
OpenBlade 修复操作不会改动已安装的共享运行时。
如果选中的共享运行时损坏，请先用 Microsoft 的 .NET 10 Desktop Runtime
安装程序修复该版本，再重试 OpenBlade 安装。

如需离线安装程序，请运行 `build/Publish.ps1 -OfflineInstaller`。
这会内嵌同一份已验证的 .NET 前置组件。Store 包应使用此选项。

开发阶段可不签名。公开发布签名构建时，请向 `Publish.ps1` 提供证书指纹。
脚本会先签署第一方负载文件，再由 WiX 嵌入，
随后签署分离的 Burn 引擎和最终安装程序。

<a id="interactive-installation"></a>

## 交互式安装

- 双击 `OpenBlade-Setup.exe` 并选择安装。
  安装程序准备修改系统时，Windows 才会请求管理员批准。
- 安装程序跟随 Windows 应用主题。在安装、修复或卸载过程中选择**显示详细信息**，
  可查看正在更改的文件、注册表项、快捷方式和服务。
- 安装或修复成功后，选择**启动 OpenBlade**。
  托盘程序会启动 Session Agent，两者均在普通用户会话中运行。
- 不要使用**以管理员身份运行**启动安装程序，否则托盘程序和 Session Agent 会继承提升权限。
  如果已经这样启动，请关闭它们，再正常启动 OpenBlade。

<a id="quiet-installation"></a>

## 静默安装

- 静默安装不显示安装界面，也不启动应用。
- `/quiet` 成功返回后，调用方自动化必须从原来的普通完整性进程启动
  已安装的 `OpenBlade.Tray.exe`，并确认托盘程序和 Session Agent 正在运行。
- 这种由调用方控制的重新启动不适用于 Microsoft Store EXE 渠道，
  因为静默调用由 Partner Center 管理。剩余问题见
  [议题 #25](https://github.com/OSSBlade/openblade-core/issues/25)。

<a id="versioned-development-builds"></a>

## 带版本的开发构建

安装多个精确开发构建而不改变基础产品版本时，请使用单调递增的 Nightly 修订号：

```powershell
.\build\Publish.ps1 -BuildChannel Nightly -BuildRevision 42 -SourceRevision (git rev-parse HEAD)
```

- 交互式安装检测到恰好一个同版本缓存安装包时，会提供**修复**和**卸载**。
  修复会用传入负载覆盖已安装文件；卸载会执行已安装包的移除流程并保留已保存的配置。
  修复会为每个安装包持久保存所有权令牌，防止即使来自同一修订的旧安装包移除新负载。
  如需同时删除已保存的配置，请关闭传入的安装程序，
  从 Windows 已安装的应用中卸载已登记的 OpenBlade，
  并在那里选择**删除已保存的配置和设置**。
- 静默 `/install` 仍将相同版本视为版本冲突，因为它无法代替用户选择维护操作。
  无人值守维护请明确使用 `/repair` 或 `/uninstall`。
- 每次精确构建验证都使用更高的 Nightly 修订号。
  不要安装产品版本相同但内容不同的 Stable 产物。

<a id="duplicate-bundle-registrations"></a>

## 重复的安装包登记

检测到多个相关 OpenBlade 安装包登记时，安装程序会在 Apply 前停止。
旧安装程序可能在 Burn 卸载时递归启动彼此，造成死锁。

- 一旧一新的混合登记也受此防护限制，因为无法确定唯一的被替换安装包。
- 交互式安装在检测后报告冲突，不提供安装或修复选项。
- 如果安装程序为其自身已登记的安装包提供卸载选项，请先使用该选项。
- 重试新版安装程序前，请通过 Windows 安装恢复移除剩余重复项。

<a id="command-line-setup-and-recovery"></a>

## 命令行安装与恢复

解包的生产包仍位于 `artifacts\win-x64\OpenBlade`，供开发和恢复使用。
需要时，在提升权限的终端中运行其安装工具。
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
- Store 调用方无法执行静默安装后所需的普通完整性重新启动，
  因此在[议题 #25](https://github.com/OSSBlade/openblade-core/issues/25)解决前，
  生命周期支持仍不完整。

包契约、认证说明、硬件声明和剩余外部发布步骤，
请参阅 [Microsoft Store 提交](../microsoft-store-submission.md)。

<a id="nightly-workflow"></a>

## Nightly 工作流

- `Nightly` GitHub Actions 工作流仅在维护者手动启动时构建最新提交的 `main`，
  没有定时调度。
- 它运行 CI 使用的格式、构建、测试、安装创作和生产发布检查，
  然后将 Actions 产物保留 14 天。
- 每次构建成功后，准备一个 GitHub 预发布草稿，包含安装程序、校验和、
  元数据和来自精确构建提交的更新日志快照。
- 工作流还会创建或更新文档拉取请求草稿，记录缺失的 Nightly 分界，
  并保留 `main` 上较新的 `Unreleased` 条目。
- 审核并合并更新日志 PR，然后在 `main` 上运行 **Finalize Nightly**，
  提供草稿的精确标签。它会先验证产物和已合并更新日志，再公开草稿。
- 已公开的 Nightly 显示在 Releases 页面。
  只有选择 **Nightly** 更新渠道时，应用才会提供这些版本。

Nightly 是测试构建。安装前，请用附带的 SHA-256 文件验证 `OpenBlade-Setup.exe`。
如想帮助测试笔记本，请按[Blade 实验性支持指南](experimental-blade-support.md)
了解可用功能和测试报告要求。所有更新渠道中，已识别型号的控制功能都会自动启用。

<a id="attestations-and-signing-status"></a>

### 构建证明与签名状态

GitHub 构建来源证明是可选项，因为并非所有仓库可见性和计费方案都支持产物证明。
确认支持后，维护者可将 `OPENBLADE_ENABLE_ATTESTATIONS` 设为 `true`，
并用以下命令验证已启用的证明：

```powershell
gh attestation verify .\OpenBlade-Setup.exe --repo OSSBlade/openblade-core
```

附带 JSON 标明精确提交、有序 Nightly 版本、构建时间、工作流运行、
安装程序哈希和 Authenticode 状态。
除非工作流已连接受保护的发布签名证书，否则安装程序预期显示 `NotSigned`，
Windows 可能弹出无法识别发布者的警告。

<a id="changelog-publication"></a>

### 更新日志发布

- 更新日志任务要求仓库启用 **Allow GitHub Actions to create and approve pull requests**。
- 该任务中的令牌权限仅限仓库内容和拉取请求。
- 如果该设置已禁用，请启用后重跑失败的更新日志任务。
  在更新日志合并并通过验证前，版本会保持草稿状态。
- 更新日志生成或附件出错会停止准备。
  如果来源快照、已合并章节、比较链接或产物不匹配，最终发布也会停止。
  请修正所报告的问题，并对同一草稿标签重新执行最终发布。
- 托管和本地发布都使用 `build/Complete-NightlyRelease.ps1`。
  可使用 `-VerifyOnly` 检查已准备的草稿而不公开发布。
