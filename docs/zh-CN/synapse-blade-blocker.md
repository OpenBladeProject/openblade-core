<a id="synapse-blade-blocker-coexistence"></a>

# 与 Synapse Blade Blocker 共存

[English](../synapse-blade-blocker.md) | 简体中文 | [Русский](../ru-RU/synapse-blade-blocker.md)

[Synapse Blade Blocker](https://github.com/OpenBladeProject/synapse-blade-blocker)
是可选的独立补丁，可让 Synapse 继续控制外设，同时排除已识别的 Blade 设备。
其仓库提供当前型号覆盖范围和补丁说明。OpenBlade 不会安装该补丁，也不依赖它。

<a id="setup"></a>

## 设置

1. 按照 Blocker 仓库的说明安装并应用补丁。
2. 重启 Synapse，使其加载修改后的文件。
3. 打开 OpenBlade。如果提示冲突，请检查补丁是否仍然生效，
   并确认没有其他 Razer 控制程序正在运行。

某些更新不会更换已修改的 AppEngine 文件，因此补丁可能继续生效。
如果 Synapse 替换了这些文件，请重新应用补丁。
OpenBlade 会拒绝与已安装文件不再匹配的补丁记录。

<a id="how-openblade-checks-coexistence"></a>

## OpenBlade 如何检查共存状态

当所有运行中的 Synapse AppEngine 实例都来自具有已验证生效补丁的安装目录，
且至少有一个打过补丁的 AppEngine 正在运行时，OpenBlade 会将这些已验证进程
从竞争控制程序检查中排除。在此已验证状态下，两个已知的 Synapse 服务别名
也不再阻止 HyperBoost 的散热控制准入。它们仍会出现在诊断中，
并继续参与其他控制程序检查。没有已验证的补丁证据时，它们会阻止 HyperBoost。

内部服务名恰好为 `RGS` 的活动 Windows 服务仍会显示，
但不会阻止 HyperBoost 的散热控制准入。仅名为 `RGS` 或
`Razer Gaming Services Updater` 的进程，或仅使用这两个显示名称之一的服务，
仍会造成阻止。其他独立 Razer 控制程序继续接受原有检查。
硬件身份、支持的命令、供电要求及其他功能准入检查仍然有效。

Synapse 也可能在界面加载后启动 `razerwdl` Windows 灯光辅助程序。
只有当其预期安装路径和产品元数据匹配、直接父进程是同一会话中已验证打过补丁的
AppEngine，且进程启动时间支持该父子关系时，OpenBlade 才会将其从控制程序发现结果中排除。
每个运行中的辅助程序都必须通过这些检查。随后，OpenBlade 在每次观察时枚举
当前存在且已启用的 Windows LampArray 接口。如果任一端点匹配已验证补丁记录中的
Blade 产品 ID、缺少端点身份，或枚举失败，辅助程序冲突就会保留。
独立启动的辅助程序即使遇到已打补丁的 AppEngine，也仍属于冲突。
这里没有辅助程序版本白名单。已检测到且 Razer 路径与产品元数据均已验证的辅助程序
不会阻止 HyperBoost 的散热控制准入；身份未验证的则会阻止。
仍被判为冲突的辅助程序继续触发其他控制程序检查。

端点检查通过 SetupAPI 读取接口元数据，不会打开 HID 设备句柄。
它匹配 HID 接口类，以及
[Windows LampArray 选择器](https://learn.microsoft.com/en-us/uwp/api/windows.devices.lights.lamparray.getdeviceselector)
使用的用法页 `0x59` 和用法 ID `1`。
结果每次刷新而不缓存，因此后续接入的设备会按现有的快照控制程序策略重新评估。
这项检查判断当前的竞争访问状态；它不会阻止辅助程序发现未来设备，也不会修改其本机代码。

验证依赖已应用补丁的记录，以及与之匹配的已安装归档和可执行文件哈希，
不依赖 Synapse 版本白名单。只要补丁工具能完成所需修改并写入有效记录，
新版本就可能无需 OpenBlade 更新即可使用。
记录缺失或格式错误、文件变化、已打补丁和未打补丁实例混用，
以及补丁应用前已启动的进程，都会保留原有冲突行为。
应用补丁后请重启 Synapse。启动期间，短暂运行的未修改启动器仍可能被报告。

请在 Synapse Blade Blocker 仓库报告 Blocker 兼容性问题。

Blocker 改变 Synapse 处理哪些设备，并不为 Synapse 提供沙箱隔离。
下列试验仅覆盖 Pro Click V2 和所列构建版本。

<a id="local-validation"></a>

## 本地验证

2026-09-19，在核心版本 466966e84c2114444684d53e2dc309ad1c7f3fc2 上，
生产控制程序防护的只读探测识别了四个正在运行的已打补丁 AppEngine 4.0.823 进程，
未返回冲突或检测错误。Blocker 版本 1261363e1c53a963f23b06d9cef554d22d5932d5
在 PowerShell 7.6.5 下成功应用并恢复补丁。
用户确认 Pro Click V2 可见、Blade 不可见，且鼠标正常工作。
之后已恢复原始归档和常规安装的 OpenBlade 环境。

已安装的 OpenBlade 二进制文件没有改变：这验证了实时补丁到防护逻辑的检测，
不代表新构建的已安装界面、所有外设功能或完整硬件隔离都通过了验证。

随后使用核心版本 `7ce1b87c` 和 Blocker 版本 `50375ea6` 的安装试验
在 Synapse 加载完成后暴露了延迟出现的 `razerwdl` 冲突。
已恢复原始归档并重启 OpenBlade。
这次失败试验取代最初快照，作为就绪程度的证据。

后续辅助程序调查发现了 LampArray 枚举和灯光操作；解析出的动态 API 查找
用于文件系统、运行时支持和 Windows Runtime 激活。
未发现独立的 HID、WinUSB 或 Razer 硬件传输。
这些证据说明该辅助程序所观察到的架构，不能保证未来厂商版本仍然如此。
本机端点探测发现 50 个当前存在且已启用的 HID 接口，无属性错误，
其中包括两个 Pro Click V2 LampArray 接口，未发现 Blade LampArray 接口；
Windows Runtime 查询独立返回了相同的两个鼠标接口。
最终安装验证必须包含已加载的 Synapse 界面和延迟启动的辅助程序。
