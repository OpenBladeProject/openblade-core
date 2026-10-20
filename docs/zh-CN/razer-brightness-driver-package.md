<a id="razer-brightness-driver-package"></a>

# Razer 亮度驱动包

[English](../razer-brightness-driver-package.md) | 简体中文 | [Русский](../ru-RU/razer-brightness-driver-package.md)

下列两款 Blade 16 如果已安装带签名的 Razer Consumer Control 驱动栈，
OpenBlade 就可以通过它调整亮度，并显示 Windows 自带的亮度提示。
打开“关于 > 设备信息”，即可查看当前型号是否能使用这套驱动。
如果不能，亮度键仍通过 WMI 调整亮度，并显示 OpenBlade 的亮度提示。

常规 OpenBlade 安装程序不会安装或移除 Razer 驱动。

<a id="runtime-hardware-scope"></a>

## 适用型号与驱动检查

两款型号的原生亮度功能分别使用各自的专用驱动栈。
OpenBlade 会检查设备的完整父级关系、服务名、实际安装的 INF 与服务的关联，以及 Windows 驱动签名和文件是否属于对应的签名目录。只要 API 仍兼容，驱动更新就无需等待 OpenBlade 更新哈希列表。文件指纹仅用于发现替换并使旧的检查结果失效；每次调整亮度前都会重新检查设备和文件身份，包括复用已找到的设备时。签名验证结果可短时缓存：

| 笔记本 | USB PID | 设备服务 |
|---|---|---|
| Razer Blade 16 (2026)，RZ09-0581 | `02E0` | `RzDev_02e0` |
| Razer Blade 16 (2025)，RZ09-0528 | `02C6` | `RzDev_02c6` |

没有可通用于所有 Razer Blade 的驱动栈。
RZ09-0581 栈包含四个针对型号的签名 INF，以及共享控制包：

| 包 | 精确硬件匹配 | 用途 |
|---|---|---|
| `rzdevu_02e0_dkm.inf` | `USB\VID_1532&PID_02E0&MI_01` | 物理设备筛选器与虚拟设备枚举器 |
| `rzdevu_02e0_vcon.inf` | `RZVIRTUAL\VID_1532&PID_02E0&MI_00&COL03` | 虚拟 Consumer Control 设备 |
| `rzdevu_02e0_kbd.inf` | `HID\VID_1532&PID_02E0&MI_01&COL01` | 键盘集合筛选器 |
| `rzdevu_02e0_mou.inf` | `HID\VID_1532&PID_02E0&MI_02` | 鼠标集合筛选器与控制设备枚举器 |
| `rzcommonu.inf` | `RAZER\DeviceContol` | 共享 Razer 控制子设备 |

最后一个标识在签名 Razer INF 中就拼写为 `DeviceContol`。
已准入的 `rzcommonu.inf` 的 SHA-256 为
`DD3C8D65C7C3CCF2093FE70FB21FB8DC724E837880566BAE629B3104A5B1B37B`，
其中原样包含 `%RzCommon.DeviceDesc% = RzCommon_Device,RAZER\DeviceContol`。
虽然 RzCommon 组件可以共用，整套驱动仍然只适用于特定型号。
其他四个包、`RzDev_02e0` 服务和二进制名称，以及其目录文件，均专属于 PID `02E0`。
修改 INF 以匹配其他 PID 会使签名目录失效，也不能证明二进制文件支持该硬件。

RZ09-0528 在已安装时使用自己的签名 Razer 栈：
`rzdevu_02c6_{dkm,vcon,kbd,mou}.inf`，服务为 `RzDev_02c6`，
最初分析的驱动包使用相同构建的 `RzCommon.sys`，以及 PID `02C6` 上相同的
`RZCONTROL`/`RZVIRTUAL` 设备形态。
最初分析的 `RzDev_02c6.sys` 的 SHA-256 为
`307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82`。
该校验值仅记录分析来源。OpenBlade 仅针对 PID `02C6` 检查每项身份。
本指南的导出工具和驱动目录中的记录仍仅适用于 `02E0`；OpenBlade 不附带这两套驱动。
其他每个型号都需要自己的签名包清单和硬件验证。

Razer 支持页面：

- [Razer Blade 16 (2026)，RZ09-0581x](https://mysupport.razer.com/app/answers/detail/a_id/20585/)
- [Razer Blade 16 (2025)，RZ09-0528x BIOS 支持](https://mysupport.razer.com/app/answers/detail/a_id/15235/)

导出的 RZ09-0581 归档、已脱敏清单，以及绑定二者的发布记录，
存放在独立的 [`blade-driver-catalog`](https://github.com/OSSBlade/blade-driver-catalog)
仓库，那里也提供其他 Razer 设备的只读导出说明。
驱动目录仓库以预发布版本提供该归档；在干净系统上的安装验证尚未完成。
运行一次 Synapse 也会安装同一套驱动。收录到驱动目录只代表已有相关证据，并不表示 OpenBlade 支持该设备。
每个型号仍需分别完成运行时和硬件验证，才能由核心程序正式接纳。

<a id="export-the-admitted-rz09-0581-package"></a>

## 导出 OpenBlade 已认可的 RZ09-0581 驱动包

`build/Export-RazerBrightnessDriverPackage.ps1` 使用 Windows PowerShell 5.1，
从已安装这套经过验证的驱动的电脑中，原样导出驱动包，供私下验证使用。
它按已发布 INF 的哈希定位每个已安装包，不依赖机器本地的 `oem###` 名称，
调用 Windows `pnputil /export-driver`，然后验证完整的三文件包、
SHA-256 哈希和 Microsoft 硬件签名。输出必须在仓库之外。

归档的 `manifest.json` 使用清单类型 `OpenBlade.RazerDriverPackageArchive`。
其 `catalogReference` 指向 `OSSBlade/blade-driver-catalog` 和
`devices/rz09-0581/1532-02e0/1.0.0.78-1.0.0.76/manifest.json`。
它有意不内嵌目录清单哈希，因为目录记录的是外层包归档哈希。
经检查的发布证明将目录清单路径及 SHA-256 与归档资源名称及 SHA-256 绑定，
避免两个文件互相计算哈希。

```powershell
.\build\Export-RazerBrightnessDriverPackage.ps1 `
    -OutputDirectory C:\tmp\Razer-02E0-Driver-Stack-1.0.0.78-1.0.0.76
```

生成的确定性 ZIP 包含五个原始 Razer 包：物理 USB 下层筛选器、
虚拟 Consumer Control 功能驱动、控制设备驱动，以及键盘和鼠标集合筛选器。
Windows 内置 HID 依赖由原始 INF 引用，不会复制。
生成的 PNF 文件和机器特定的设备实例会被排除。

不要在其他 Blade 型号上安装此包，也不要削弱 OpenBlade 的精确设备和二进制检查来让其加载。

<a id="native-windows-installation-path"></a>

## 使用 Windows 自带工具安装

Windows 自带 PnPUtil，可将签名包暂存到 Driver Store，并安装到匹配设备。
Microsoft 的 [PnPUtil 命令参考](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)
记录了 `/add-driver`、`/install`、`/subdirs` 和 `/reboot`。

以下步骤用于在具备恢复手段的 RZ09-0581 干净系统上验证安装，目前不供普通用户自行安装。
该驱动栈会介入内置键盘和鼠标的输入处理，重启 USB 设备树的部分节点，还可能要求重启系统。
测试前请接通交流电、连接外置 USB 键盘和鼠标，并确认发生问题时仍能进行恢复操作。

开始修改前，请以管理员身份打开 PowerShell，核对设备和驱动包：

1. `HKLM:\HARDWARE\DESCRIPTION\System\BIOS` 必须报告制造商为 Razer，
   `SystemProductName` 为 `Blade 16 - RZ09-0581`。
2. Windows 必须报告一个已连接的 `USB\VID_1532&PID_02E0&MI_01` 设备，
   和一个已连接的 `USB\VID_1532&PID_02E0&MI_02` 设备。
   如果找不到设备、发现重复设备，或设备标识不符，请停止操作。
3. ZIP 名称和 SHA-256 必须匹配经检查的发布证明。
   同一证明必须按仓库路径和 SHA-256 标明目录清单。
   解压后，每个文件必须匹配 `SHA256SUMS.txt`，
   且每个 INF、CAT 和 SYS 的签名都必须有效，
   并由 Microsoft Windows Hardware Compatibility Publisher 签发。
   在有 SignTool 的验证主机上，使用 `signtool verify /kp /c <catalog> <INF-or-SYS>`
   确认 INF 或 SYS 文件确实属于该签名目录。Microsoft 在
   [SignTool 参考](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/signtool)
   中记录了 `/c`。

暂存任何内容前，请在包和仓库之外创建日志。
记录 BIOS 身份、已连接的匹配实例、`pnputil /enum-drivers /files` 的输出、
当前绑定和驱动栈，以及当前 `RZVIRTUAL` 和 `RZCONTROL` 树。
保留原始输出，不要只写手工摘要。

下面的辅助函数会将每条 PnPUtil 命令、输出和退出码附加到日志。
先设置 `$packageRoot` 和 `$journalDirectory`。
任何非零退出码后都应停止。不要重试结果不明确的操作。

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

先暂存全部五个签名包，不进行绑定：

```powershell
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_vcon\rzdevu_02e0_vcon.inf")
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzcommonu\rzcommonu.inf")
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_dkm\rzdevu_02e0_dkm.inf")
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_kbd\rzdevu_02e0_kbd.inf")
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_mou\rzdevu_02e0_mou.inf")
```

每条命令后，将新报告的发布名称和包身份复制到日志，
注明该包在安装前是否已存在。回滚时据此判断哪些包由本次测试安装。

五个包全部成功暂存后，按依赖顺序绑定：

```powershell
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_dkm\rzdevu_02e0_dkm.inf", '/install')
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_vcon\rzdevu_02e0_vcon.inf", '/install')
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_kbd\rzdevu_02e0_kbd.inf", '/install')
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzdevu_02e0_mou\rzdevu_02e0_mou.inf", '/install')
Invoke-PnpLogged @('/add-driver', "$packageRoot\rzcommonu\rzcommonu.inf", '/install')
```

验证时不要在这些命令中加入 `/reboot`。
如果 Windows 要求重启，请停止后续操作，妥善保存日志后再安排重启。
PnPUtil 不会将排名较低的驱动强制装到设备上，因此退出码为零不能替代安装后检查。

安装后，验证精确的 `RZVIRTUAL` 和 `RZCONTROL` 子设备、
以及它们的服务和驱动栈，并确认设备管理器没有报告问题。
重启 OpenBlade，打开“关于 > 设备信息”查看结果。
Razer 亮度驱动行应显示 `Available · Windows OSD`（中文界面为“可用 · Windows 系统提示”）。
实际测试亮度键的按下、长按和松开，确认亮度能够调整，且显示的是 Windows 自带的亮度提示。

不要使用大范围设备移除、`/force` 或按设备类整体重启命令。
回滚时，只能移除已保存的安装前日志能够证明由本次测试新增的已发布驱动包。
按以下顺序移除：RzCommon、VCon、鼠标、键盘、DKM。
使用 `pnputil /delete-driver <journal-owned-oem.inf> /uninstall`；
如果 Windows 报告包正在使用或需要重启，请停止。

发布一键安装程序前，必须在 RZ09-0581 干净系统上完成两次安装验证。
还需逐一验证每个包安装失败后的恢复、按相反顺序回滚后内置键盘和触摸板的恢复、
重复修复的幂等性、重启与睡眠周期，以及实际长按和松开亮度键的表现。
上述检查通过前，这些 Windows 命令只能用于受控验证。

导出文件仍属 Razer 财产，不受 OpenBlade 的 GPL 许可证覆盖。
将归档附到公开版本前，必须保留涵盖精确文件和分发方式的 Razer 书面授权，
并随归档原样提供 `THIRD-PARTY-NOTICE.txt`。
