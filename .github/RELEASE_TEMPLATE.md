# ModbusTool vX.Y.Z

**作者：工控菜鸡刘** · 开源协议：MIT

## 本次更新

- 

## 下载

| 产物 | 适用系统 | 说明 |
|---|---|---|
| `Setup/ModbusTool-vX.Y.Z-Setup-net8.exe` | Windows 10 1809+ / 11 | 安装版，快捷方式 / `.mbwork` 关联 / 卸载入口，需 .NET 8 Desktop Runtime |
| `Setup/ModbusTool-vX.Y.Z-Setup-net6.exe` | Windows 7 SP1+ | 旧系统安装版，需 .NET 6 Desktop Runtime |
| `Portable/ModbusTool-vX.Y.Z-Portable-net8-fd.7z` | Windows 10 1809+ / 11 | 免安装框架依赖，需 .NET 8 Desktop Runtime，体积最小（推荐） |
| `Portable/ModbusTool-vX.Y.Z-Portable-net8-sc.7z` | Windows 10 1809+ / 11 | 免安装自包含，离线即用，无需预装 .NET |
| `Portable/ModbusTool-vX.Y.Z-Portable-net6-fd.7z` | Windows 7 SP1+ | 免安装框架依赖，需 .NET 6 Desktop Runtime |
| `Portable/ModbusTool-vX.Y.Z-Portable-net6-sc.7z` | Windows 7 SP1+ | 免安装自包含，离线即用 |
| `ModbusTool-vX.Y.Z-Source.zip` | 开发者 | 源码包 |

> 各 Portable 产物同时提供 `.zip` 与 `.7z`，`.7z` 为固实压缩、体积更小，推荐分发。可直接复制 `publish/RELEASE-COPY.txt` 作为发布文案（含分组与适用系统说明）。

校验文件：`SHA256SUMS.txt`（相对 `publish/` 的路径，含 Setup/Portable 分层）。

## 验证

- Release 双目标构建 0 警告 0 错误。
- net8 与 net6 各 119 项单元测试全部通过。
- 本机 TCP 环回、趋势、主题与配置读写冒烟通过。

## 已知限制

- 仅支持 Windows x64。
- Windows 7 需 SP1 与 KB2533623，串口功能受系统驱动限制。
- 自包含包首次启动需解压原生依赖，启动略慢。

## 反馈

请附版本、系统、TCP/RTU 配置、站号、功能码、复现步骤与脱敏日志，提交至仓库 Issues。

---

作者：工控菜鸡刘 · Copyright (c) 2026 工控菜鸡刘 · MIT License
