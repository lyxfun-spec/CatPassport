# 小猫助手正式版安装程序

此目录包含小猫助手 Windows 正式发行安装器。安装器由两个层次组成：

- `CatAssistant.Setup`：自包含的 WPF 安装向导，复用桌面应用的色彩、圆角、字体和动效语言。
- `CatAssistant.Package`：WiX/Windows Installer 事务型 MSI，负责文件写入、回滚、修复、升级和卸载注册。

最终交付物是一个嵌入 MSI 的 `小猫助手-<版本>-安装程序.exe`，目标电脑不需要预装 .NET 或 WiX。

## 构建

在仓库根目录执行：

```powershell
.\installer\Build-Installer.ps1 -Version 1.0.0
```

脚本会依次：

1. 使用 Electron Forge 生成自包含桌面应用；
2. 使用仓库固定的 WiX SDK 生成 MSI；
3. 将 MSI 嵌入 WPF 安装向导；
4. 生成单文件安装程序和 SHA-256 校验文件。

若应用产物已经存在，可使用 `-SkipAppPackage`。该选项仍会检查 `windows/CatAssistant.Desktop/out/小猫助手-win32-x64/CatAssistant.exe` 是否存在。

## 正式签名

正式发布必须同时签名 MSI 和最终 EXE：

```powershell
.\installer\Build-Installer.ps1 `
  -Version 1.0.0 `
  -CertificateThumbprint '<代码签名证书指纹>'
```

构建机需要安装 Windows SDK，以提供 `signtool.exe`。不要把证书密码、PFX 文件或时间戳凭据提交到仓库。

## 验证

```powershell
.\installer\Test-Installer.ps1
.\installer\Test-Installer.ps1 -RequireSigned  # 正式发布门禁
```

完整发布验收还应在干净的 Windows 11 x64 虚拟机执行：

- 首次安装、取消 UAC、取消安装和安装失败回滚；
- 从上一正式版本覆盖升级、阻止降级；
- 同版本安装包覆盖现有安装，并清理由开发构建留下的重复安装记录；
- Windows“已安装的应用”中的修复和卸载；
- 安装前应用正在运行、磁盘空间不足和不可写目录；
- 升级/卸载后 `%LOCALAPPDATA%\FoloToy\CatAssistant` 中的用户数据仍然存在；
- 桌面/开始菜单快捷方式、首次启动、蓝牙权限和防病毒软件扫描；
- 安装程序与 MSI 的 Authenticode 签名、时间戳和 SHA-256 校验。

## 设计边界

- 安装器只安装程序，不收集 API Key、不登录模型服务、不执行设备配对。
- 首次使用配置继续由小猫助手应用内引导完成。
- 安装过程使用 Windows Installer 的真实事务和回滚；界面不展示伪造百分比。
- 安装阶段通过提升权限的内部工作进程订阅 Windows Installer `INSTALLMESSAGE_PROGRESS` 记录，向主界面回传真实百分比；流动高光只表示仍在工作，不改变进度值。
- 日志写入 `%LOCALAPPDATA%\FoloToy\CatAssistant\InstallerLogs`，错误页可直接打开所在位置。
- 当前安装包目标为 Windows 10 2004 及以上的 x64 系统。
- 版本策略为：未安装时全新安装；相同版本覆盖安装；安装包版本更高时正常升级；电脑中的版本更高时提示并取消，MSI 层也会阻止降级。
- MSI 抑制 ICE60 与 ICE61：前者来自 Electron 发行目录中没有版本/语言元数据的二进制资源；后者是允许同版本开发构建执行 Major Upgrade 时的预期告警。构建版本严格限制为三个数字字段，避免该兼容策略造成第四字段版本的意外降级。
