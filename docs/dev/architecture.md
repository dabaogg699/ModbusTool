# 架构简述

作者：工控菜鸡刘 · Copyright (c) 2026 工控菜鸡刘 · MIT License

## 模块依赖

```text
ModbusMasterApp ─┐
                 ├─> ModbusTool.App ─┬─> ModbusMaster ─┐
ModbusSlaveApp ──┘                   ├─> ModbusSlave  ─┤
                                     └─> ModbusCore  <──┘

ModbusTool.Updater ─> ModbusTool.Update
```

- `ModbusCore`：请求/响应 PDU、TCP/RTU 帧、CRC、字节序、数据区、传输抽象与结构化日志，不依赖 WPF。
- `ModbusMaster`：有界优先命令队列、周期轮询、超时重试/重连、工作区和趋势缓冲。
- `ModbusSlave`：监听、请求处理、站管理、数据仿真和故障注入。
- `ModbusTool.App`：共享 View、ViewModel、控件与主题。独立入口项目只负责窗口和应用生命周期。

依赖方向保持向下，协议层不得反向引用界面。

## 主站调度与写入时序

`MasterService` 只有一个命令执行循环。`MasterCommandQueue` 以“优先级 + 入队序号”排序：手动读写优先于周期轮询，同优先级保持 FIFO。队列和日志/UI 缓存均有上限，避免异常现场持续占用内存。

双击写入流程：

1. `MasterViewModel.WriteRegisterValueAsync` 以 `(UnitId, Address)` 获取当前会话的 `WritePending` 锁。
2. 输入值立即写入本地预览，行状态显示“写入中”，随后下发功能码 05/06 请求。
3. 周期轮询刷新和错误标色遇到该键仍处于 pending 时跳过，不覆盖预览。
4. 正常或异常写应答进入与轮询相同的 FIFO 响应队列；`ProcessWriteResponse` 消费到写应答后才解锁并回填真实值，因此更早入队的轮询也不能覆盖已完成写入。
5. 超时、传输异常或从站异常立即解锁，行状态显示“写入失败”。每个 `MasterViewModel` 有独立锁表，站号和地址共同构成键，不会跨会话或跨站误锁。

## 跨线程模型

- 传输/协议事件产生于通信后台任务。
- `MasterViewModel` 使用 `ConcurrentQueue<MasterResponseEventArgs>` 和错误队列，DispatcherTimer 在 UI 线程消费；Slave 使用 UI Action 队列。
- WPF 控件、绑定集合和 ScottPlot 只能在 UI 线程访问。通信回调只写入线程安全队列或普通状态。
- `TrendService` 在锁内更新有界样本，在锁外发布事件；UI 再经队列/定时器合并到图表。
- 日志写入采用有界 Channel，单后台任务负责文件 IO；UI 接收事件后也先入队，按 200 ms 小批量刷新。

## 趋势数据流

```text
ModbusResponse
  → MasterService 解码点位原始值与工程量
  → TrendService.Record / MarkBreak
  → MasterViewModel 采样事件与 UI 队列
  → MasterTrendView
  → TrendChart（ScottPlot）
```

需要保持的兼容约束：

- 曲线颜色使用最小空闲调色板索引，重建系列不能重置用户样式或图例显隐。
- ScottPlot 时间轴使用本地时间，与 CSV 导出和十字准星读数一致。
- 同值压缩保留，恒值点位每 2 秒发心跳样本，断连用 break 断开线条。
- 单曲线与视图缓存有界，暂停只影响图表追加，不停止协议采集。

## 日志数据流

```text
协议/写入/连接事件
  → ToolLogEntry + ToolLogContext
  → RollingFileLogService 有界 Channel
  → 单后台写入任务
  → 按日期、大小滚动；默认保留 5 个文件
  → LogView 有界历史（5000）与每页 300 条滚动加载
  → 筛选或导出选中/全部 CSV
```

Trace 记录正常通信与原始报文；Debug 默认关闭，可在日志面板开启。Error 的 Details 保存异常堆栈，其他级别只保留异常消息。文件大小与保留数量可在日志面板调整。

## 会话与共享资源

每个主站会话拥有自己的 `MasterService`、工作区、趋势服务、响应队列和 WritePending 锁表。窗口级日志服务由会话共享，但每条通信日志包含独立 `SessionId`。串口租约注册表是进程级共享资源，只用于阻止两个会话同时打开同一 COM 口。

## 配置与版本

- 主站工作区与从站配置使用 JSON；旧格式只通过迁移器读取。
- 默认运行数据位于程序目录，文档和脚本使用相对路径。
- `version.json` 是唯一版本源，`Directory.Build.props` 由脚本同步，csproj 不写版本号。

## 双目标与发布

```text
src / tests
  ├─ net8.0(-windows)  → ScottPlot.WPF 5.1.59 → 主版本（Windows 10 1809+）
  └─ net6.0(-windows)  → ScottPlot.WPF 5.0.55 → 旧系统版（Windows 7 SP1+）

build/compat
  ├─ RequiredMembers.cs   # net6 的 required 编译特性
  ├─ ArgumentGuard.cs     # .NET 7/8 参数与 Stopwatch API 等价实现
  └─ StreamReadExactly.cs # net6 测试的 ReadExactlyAsync 扩展
```

- 所有业务项目与测试项目同时面向 net8 与 net6；协议、调度、趋势数据模型与 ViewModel 共用同一份源码。
- 目标框架通过 `TargetFrameworks` 声明，ScottPlot 版本由 `ModbusTool.App.csproj` 按 `TargetFramework` 条件引用。
- `build/compat` 只由 `Directory.Build.targets` 在内层 net6 构建中链接，net8 主版本不引入兼容代码。
- 发布脚本分别产出框架依赖与自包含产物；两者共享相同业务程序集，仅运行时携带方式不同。
- 安装包基于框架依赖产物，安装前检测对应 Desktop Runtime；自包含包用于离线与旧系统环境。
