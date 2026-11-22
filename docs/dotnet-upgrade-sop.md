# .NET 版本升级 SOP

作者：工控菜鸡刘 · Copyright (c) 2026 工控菜鸡刘 · MIT License

本文档定义 ModbusTool 新增 .NET 目标框架（TFM）的标准流程，例如从 net8 主线升级到 net9/net10。核心策略：**新增新版 TFM 作为主线，旧 TFM 保留为兼容维护分支，不一次性替换旧版本**。任何接手者按本流程逐步执行即可完成升级，不破坏双/多目标编译、兼容层、打包脚本与测试体系。

## 0. 升级原则

1. 新功能只在最新主线 TFM 开发；旧兼容 TFM（当前 net6，用于 Win7 SP1+）**只合并 P0 阻塞 Bug 修复，不新增功能**。
2. 旧 TFM 的 NuGet 依赖（ScottPlot 5.0.55 + SkiaSharp 2.88.9）**禁止升级**，避免 Win7 渲染崩溃；新版 TFM 使用新稳定版。
3. WPF 永远不做裁剪（`PublishTrimmed=false`），历史踩坑：裁剪会误删主题资源与控件样式。
4. 每一步完成即提交，失败可回退单步，不做大爆炸式合并。

## 1. 前置确认

| 检查项 | 说明 |
|---|---|
| 目标 TFM 的最低 Windows 版本 | 例如 net9 桌面 = Windows 10 1809+，net10 = Windows 10 21H2+；写入 compatibility.md |
| SDK 安装 | 固定目录安装新 SDK（`dotnet-install.ps1 -Channel 9.0 -InstallDir D:\tools\dotnet-sdk-9 -NoPath`），不覆盖旧 SDK |
| 依赖库对新 TFM 的资产支持 | 确认 ScottPlot.WPF 等提供对应 `netX.0-windows` 资产；WPF 库必须用 `-windows` 变体 |
| EOS 检查 | 被替代的旧 TFM 是否已到微软支持终止日；EOS 的 TFM 评估下架，下架前一个版本在文档标注 |

## 2. Step1：解决方案与项目文件

1. `Directory.Build.props`：`TargetFrameworks` 增加 `net9.0-windows`（WPF 项目）/ `net9.0`（库与测试项目），保持升序排列。
2. `Directory.Build.targets` 或各 csproj 的条件引用：按 TFM 区分包版本。
   - 新 TFM：ScottPlot 最新稳定版及其传递依赖（SkiaSharp 等）。
   - net6 兼容分支：保持 ScottPlot 5.0.55 + SkiaSharp 2.88.9 不变。
3. `build/compat/` 兼容层：
   - 新 TFM 若引入旧 TFM（net6）不存在的 API，继续在兼容层补等价实现；
   - 检查是否有兼容代码在所有现存 TFM 上都已原生可用，可删除冗余实现（删除前跑全量测试）。
4. 编译验证：`dotnet build ModbusTool.sln -c Release` 要求 0 警告 0 错误（`.NET EOL` 提示用 `CheckEolTargetFramework=false` 关闭）。

## 3. Step2：发布脚本 scripts/publish.ps1

1. `$variants` 数组新增变体（示例）：

```powershell
[pscustomobject]@{
    Name = "net9"; Framework = "net9.0-windows"; ShortName = "net9"
    MinWindows = "10.0.17763"; RuntimeMajor = "9.0"
    RuntimeName = ".NET 9 Desktop Runtime"; TargetOs = "Win10 1809+ / Win11"
}
```

2. 字段说明（新命名规则，G13 起生效）：
   - `ShortName`：产物文件名中的 TFM 简称（`ModbusTool-v1.2.0-Setup-net9.exe`）。
   - `MinWindows`：传给 Inno 的 `MinVersion`。
   - `RuntimeMajor`：Inno 运行时检测的主版本号。
   - `TargetOs`：RELEASE-COPY.txt 文案中的适用系统描述。
3. SHA256SUMS.txt 与 RELEASE-COPY.txt 由脚本按 `$variants` 自动生成，无需手工维护。
4. 版本登记（version.json.released）逻辑不变。

## 4. Step3：Inno Setup 脚本 installer/ModbusTool.iss

1. `[Setup]` 段新增该 TFM 的 `AppId` GUID（每个变体独立 GUID，避免互相覆盖卸载）。
2. `InitializeSetup` 的运行时检测是按 `RuntimeMajor` 参数化的，通常无需改逻辑；确认 `RuntimeInstalled` 对新主版本目录（`{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\9.0.x`）可正常匹配。
3. 缺失运行时引导下载链接：`https://dotnet.microsoft.com/download/dotnet/9.0`，按变体加入。
4. `MinVersion` 与快捷方式、`.mbwork` 文件关联逻辑无需改动。

## 5. Step4：文档更新

| 文档 | 更新内容 |
|---|---|
| `docs/dev/compatibility.md` | 目标系统表新增 TFM 最低 Windows 版本；第三方依赖差异表新增库版本；验证矩阵新增列；兼容性变更记录追加一行 |
| `docs/dev/build.md` | 环境准备新增 SDK 版本；产物命名示例补充新 TFM |
| `README.md`、`docs/user/manual.md` | 下载区与系统要求补充新 TFM 产物与适用系统 |
| `.github/RELEASE_TEMPLATE.md` | 下载表新增新 TFM 行 |
| `CHANGELOG.md` | bump 新版本后记录 .NET 升级与依赖库版本变更 |

## 6. Step5：回归测试（必做，按序）

1. 全解决方案多 TFM Release 编译：0 警告 0 错误。
2. 全量单元测试：`dotnet test ModbusTool.sln -c Release --no-build`，所有 TFM 全部通过。
3. 本机环回稳定性：`127.0.0.1:502` 多站轮询 ≥10 分钟，0 错误、0 超时；趋势绘图、报文监控、主题切换穿插执行。
4. 新 TFM 趋势渲染冒烟（ScottPlot 新版出图与 PNG 导出）。
5. `publish.ps1 -NoRegister` 演练打包：核对产物命名、Setup/Portable 目录、SHA256SUMS、RELEASE-COPY。
6. 安装包：安装、自定义路径、快捷方式、文件关联、启动、卸载清理（本机可用静默参数验证）。
7. 新 TFM 最低版本 Windows 实机验证（如 net9 → Win10 21H2）；旧 TFM（net6）在 Win7 抽测启动与渲染，防止改动引入兼容 Bug。

## 7. Step6：版本分支与下架管理

1. 每个活跃 TFM 的定位写入 compatibility.md：**主线**（接收新功能）/ **仅修复**（只合 P0 Bug）/ **待下架**（EOS 前一个版本标注）。
2. 微软某 .NET 版本 EOS 后：下一个 MINOR 版本评估移除该 TFM；移除时同步清理 `$variants`、csproj 条件引用、compat 兼容层、Inno AppId 与文档行。
3. 下架前保持至少一个版本的发布物仍可供下载（Release 资产不删除）。

## 8. 检查清单（复制即用）

- [ ] Directory.Build.props 增加 TFM
- [ ] 条件包版本：新 TFM 新版库，net6 锁定不动
- [ ] build/compat 增补 / 清理
- [ ] Release 编译 0 警告 0 错误
- [ ] 全量单元测试通过
- [ ] publish.ps1 `$variants` 新增变体
- [ ] ModbusTool.iss 新增 AppId / 下载链接
- [ ] compatibility.md / build.md / README / RELEASE_TEMPLATE / CHANGELOG 更新
- [ ] 环回稳定性 ≥10 分钟
- [ ] 打包演练（-NoRegister）核对命名与文案
- [ ] 安装包安装/卸载验证
- [ ] 最低版本 Windows 实机验证

---

作者：工控菜鸡刘 · Copyright (c) 2026 工控菜鸡刘 · MIT License
