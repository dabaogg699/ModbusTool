# 构建与发布

作者：工控菜鸡刘 · Copyright (c) 2026 工控菜鸡刘 · MIT License

本文档说明 ModbusTool 的开发构建、双目标发布、安装包生成与交付物校验。作者：工控菜鸡刘。

## 1. 环境准备

| 工具 | 版本 | 用途 |
|---|---|---|
| Windows | x64 | WPF 与串口 |
| .NET SDK | 8.0.4xx | 编译 net8 与 net6 目标 |
| .NET 6 Desktop Runtime | 6.0.x | 运行或测试 net6 目标 |
| .NET 8 Desktop Runtime | 8.0.x | 运行 net8 框架依赖产物 |
| 7-Zip | 任意较新版本 | 可选，生成固实压缩 `.7z` |
| Inno Setup | 6.x | 可选，生成安装包 |

验证 SDK：

```powershell
dotnet --list-sdks
dotnet --list-runtimes
```

如果本机没有 SDK，可使用固定目录安装，避免临时目录被清理：

```powershell
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile "$env:TEMP\dotnet-install.ps1"
powershell -ExecutionPolicy Bypass -File "$env:TEMP\dotnet-install.ps1" -Channel 8.0 -Quality GA -InstallDir D:\tools\dotnet-sdk-8 -NoPath
```

## 2. 开发构建

```powershell
dotnet restore ModbusTool.sln
dotnet build ModbusTool.sln -c Release
dotnet test ModbusTool.sln -c Release --no-build
```

- 解决方案内所有 src 与 tests 项目均为 `net8.0` / `net6.0` 双目标。
- net8 使用 ScottPlot.WPF 5.1.59；net6 使用 ScottPlot.WPF 5.0.55 与 SkiaSharp 2.88.9。
- net6 的编译器特性与 API 兼容代码位于 `build/compat/`，由 `Directory.Build.targets` 按目标框架链接。
- 构建要求 0 警告、0 错误；`CheckEolTargetFramework=false` 仅用于关闭 .NET 6 EOL 提示，不改变运行时行为。

## 3. 版本管理

```powershell
powershell -ExecutionPolicy Bypass -File scripts\version.ps1 -Command show
powershell -ExecutionPolicy Bypass -File scripts\version.ps1 -Command bump -Part patch -Summary "修复说明"
powershell -ExecutionPolicy Bypass -File scripts\version.ps1 -Command validate
```

`version.json` 是唯一版本源；`Directory.Build.props` 由脚本同步；`CHANGELOG.md` 必须有对应版本小节，否则发布脚本中止。

## 4. 发布脚本

```powershell
# 默认：net8/net6 × 框架依赖/自包含，生成 ZIP、7z（可用时）、源码包与 SHA256
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

# 只生成框架依赖包
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Mode FrameworkDependent

# 只生成自包含包
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Mode SelfContained

# 额外生成 Inno Setup 安装包
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Installer

# 指定 Inno Setup 编译器位置
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Installer -InnoSetupPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

# 已发布版本在装好 Inno 后补产安装包（不重新打包 ZIP、不登记版本）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -InstallerOnly

# 生成独立符号包
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Symbols
```

主要参数：

| 参数 | 说明 |
|---|---|
| `-Mode` | `Both`（默认）、`FrameworkDependent`、`SelfContained` |
| `-OutputDirectory` | 默认仓库 `publish/`，每次发布前清空 |
| `-Runtime` | 默认 `win-x64` |
| `-Installer` | 生成 net8/net6 安装包 |
| `-InstallerOnly` | 仅为已发布版本补产安装包，保留现有 ZIP，不登记版本 |
| `-InnoSetupPath` | ISCC.exe 路径 |
| `-NoZip` | 只保留解压目录 |
| `-Symbols` | 单独生成 PDB 符号包 |
| `-NoRegister` | 只验证构建与打包，不写入 `version.json.released` |
| `-DotnetPath` | 指定 dotnet 可执行文件 |

### 4.1 两种发布模式

**框架依赖**

- `--self-contained false`、`PublishSingleFile=false`。
- 程序目录只含自身 dll、依赖与资源，体积最小。
- 目标机必须安装对应 Desktop Runtime。

**自包含**

- `--self-contained true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true`、`EnableCompressionInSingleFile=true`。
- 单个 exe 内置运行时与原生依赖，离线可用；内嵌程序集压缩降低分发体积，首次启动略慢。
- 不做裁剪（`PublishTrimmed=false`），避免 WPF 主题、控件样式与反射资源被误删。

### 4.2 产物命名（G13 起生效）

产物按 **Setup（安装版）** 与 **Portable（免安装版）** 分目录存放；`-fd` = 框架依赖，`-sc` = 自包含：

```text
Setup/ModbusTool-v<版本>-Setup-net8.exe              # 安装版，仅框架依赖
Setup/ModbusTool-v<版本>-Setup-net6.exe
Portable/ModbusTool-v<版本>-Portable-net8-fd.7z      # 免安装框架依赖（另有同名 .zip）
Portable/ModbusTool-v<版本>-Portable-net8-sc.7z      # 免安装自包含（另有同名 .zip）
Portable/ModbusTool-v<版本>-Portable-net6-fd.7z
Portable/ModbusTool-v<版本>-Portable-net6-sc.7z
ModbusTool-v<版本>-Source.zip                        # 源码包
ModbusTool-v<版本>-symbols.zip                       # 符号包（仅 -Symbols 时生成）
SHA256SUMS.txt                                       # 相对路径哈希，含 Setup/Portable 分层
RELEASE-COPY.txt                                     # Release 页展示文案，可直接复制
```

主从站放在同一程序目录，共享依赖文件；发布包仅携带 `docs/help` 与 `docs/user/manual.md`，不复制开发文档与历史归档。

### 4.3 用户数据目录（G14 起）

安装目录只读，所有写入一律走 `%APPDATA%\ModbusTool`（`templates` / `workspaces` / `runtime-cache` / `logs`），路径统一由 `ModbusTool.App/Services/AppDataPathHelper.cs` 提供。新增写文件功能时禁止使用 `AppContext.BaseDirectory` / `AppDomain.CurrentDomain.BaseDirectory` 作为写入根；内置默认模板位于 `src/ModbusTool.App/Assets/Templates/default-template.mbwork`，以嵌入资源打包，修改模板内容后重新编译即可生效。

## 5. 安装包

安装脚本：`installer/ModbusTool.iss`。

- 默认目录：`C:\Program Files\ModbusTool`，可在向导中修改。
- 开始菜单：ModbusMaster、ModbusSlave、卸载入口。
- 可选任务：桌面快捷方式、`.mbwork` 工作区文件关联。
- 控制面板卸载由 Inno Setup 自动登记。
- net8 安装包要求 Windows 10 1809+；net6 安装包要求 Windows 7 SP1+。
- 安装前检测 `Microsoft.WindowsDesktop.App` 对应主版本；缺失时弹窗引导打开官方下载页并中止安装。

手工编译：

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
  "/DAppVariant=net8" `
  "/DAppVersion=1.1.7" `
  "/DSourceDir=D:\Desktop\ModbusTest\publish\.staging\net8-framework-dependent" `
  "/DOutputDir=D:\Desktop\ModbusTest\publish\Setup" `
  "/DOutputBaseFilename=ModbusTool-v1.1.7-Setup-net8" `
  installer\ModbusTool.iss
```

通常不需要手工调用，`publish.ps1 -Installer` 会传入正确参数。

> 若 Inno Setup 安装目录的 `Languages` 下缺少 `ChineseSimplified.isl`，删除 `ModbusTool.iss` 的 `[Languages]` 段即可使用默认语言编译；也可从 [issrc 仓库](https://github.com/jrsoftware/issrc/blob/main/Files/Languages/ChineseSimplified.isl) 下载后放入 `Languages` 目录。

> 2026-03 起 Inno Setup 官方下载链接迁移至 GitHub Releases（https://github.com/jrsoftware/issrc/releases），`files.jrsoftware.org` 仅保留签名文件；离线安装时从 GitHub Releases 下载 `innosetup-6.x.x.exe` 静默安装即可：`innosetup-6.x.x.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER`。

## 6. 校验交付物

```powershell
Get-FileHash publish\*.zip, publish\*.exe, publish\*.7z -Algorithm SHA256 |
  Select-Object Hash, Path
Get-Content publish\SHA256SUMS.txt
```

建议在干净目录解压后验证：

1. 框架依赖包：目标机未装运行时时应提示安装；安装后可启动主从站。
2. 自包含包：断网、未安装 .NET 的机器可直接启动。
3. 安装包：可修改目录、创建快捷方式、从控制面板卸载。
4. 主站与从站环回 `127.0.0.1:502` 至少 10 分钟，错误与超时为 0。

## 7. 常见问题

- **找不到 ISCC.exe**：安装 Inno Setup 6，或用 `-InnoSetupPath` 指定。
- **net6 测试无法运行**：安装 .NET 6 Desktop Runtime。
- **框架依赖包启动失败**：确认安装的是 Desktop Runtime，而不是 ASP.NET Core Runtime。
- **7z 未生成**：安装 7-Zip 或设置 `SEVENZIP_PATH`；不影响 ZIP。
- **体积异常**：确认没有把 `bin`、`obj`、`docs/archive` 或符号文件打进发布包。

---

作者：工控菜鸡刘 · Copyright (c) 2026 工控菜鸡刘 · MIT License
