// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Reflection;

namespace ModbusTool.App.ViewModels;

/// <summary>帮助条目：左侧标签 + 右侧说明，用于卡片内的分行展示。</summary>
public sealed record HelpEntry(string Label, string Text);

/// <summary>帮助卡片：标题 + 可选概述段落 + 分行条目。</summary>
public sealed record HelpSection(string Title, string Summary, IReadOnlyList<HelpEntry> Entries)
{
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
}

/// <summary>
/// 【帮助】窗口数据源：与【关于】窗口共用卡片式版式，按主站/从站分别提供概述、功能说明与参数说明。
/// 文本与 docs/help 下的在线文档保持一致；版本号取当前入口程序集。
/// </summary>
public sealed class HelpViewModel
{
    public HelpViewModel(string productName, string productKind)
    {
        ProductName = string.IsNullOrWhiteSpace(productName) ? "ModbusTool" : productName.Trim();
        bool isSlave = string.Equals(productKind?.Trim(), "从站", StringComparison.Ordinal);
        ProductKind = isSlave ? "从站" : "主站";
        Assembly entry = Assembly.GetEntryAssembly() ?? typeof(HelpViewModel).Assembly;
        Version = NormalizeVersion(
            entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            entry.GetName().Version);
        Title = $"{ProductName} 帮助";
        Subtitle = isSlave ? "从站仿真配置、故障注入与参数说明" : "主站调试、点位写入与参数说明";
        Sections = isSlave ? BuildSlaveSections() : BuildMasterSections();
    }

    public string ProductName { get; }

    public string ProductKind { get; }

    public string Version { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<HelpSection> Sections { get; }

    private static IReadOnlyList<HelpSection> BuildMasterSections()
    {
        return
        [
            new HelpSection(
                "基础概述",
                "ModbusMaster 是面向工业现场的主站调试工具，支持 Modbus TCP 与 Modbus RTU 串口通信，可对 PLC、变频器、仪表、智能模块进行协议调试、仿真、压力测试与异常报文验证。阅读顺序建议先看功能说明了解界面分区，再按参数说明调整超时、重试与轮询策略。",
                []),
            new HelpSection(
                "功能说明",
                string.Empty,
                [
                    new HelpEntry("主站调试", "多Tab会话隔离，每个会话独立连接、独立轮询、独立趋势、独立报文监视，会话之间互不干扰。"),
                    new HelpEntry("点位配置", "支持线圈、离散输入、保持寄存器、输入寄存器点位配置；支持CSV点位表导入导出，可自定义数据类型、字节序。"),
                    new HelpEntry("点位写入", "双击点位单元格下发写入指令；写入等待应答期间点位锁定，不会被轮询数据覆盖；应答成功后同步刷新寄存器数值。"),
                    new HelpEntry("历史趋势", "基于ScottPlot实时曲线渲染，支持图例曲线显隐、滚轮缩放、拖拽平移、十字准星读数；支持曲线图片、CSV数据导出，恒定值点位自动补点。"),
                    new HelpEntry("报文监视器", "支持按方向、站号、功能码筛选报文；CRC错误标红、异常响应标橙。"),
                    new HelpEntry("日志查看", "按日志级别、分类、关键字筛选，支持日志导出，携带会话ID、站号、功能码、报文摘要、通信耗时。"),
                    new HelpEntry("通信设置", "TCP默认127.0.0.1:502本地环回测试；RTU串口预置常用波特率，支持自定义非标波特率。"),
                    new HelpEntry("资源释放", "关闭Tab标签自动释放串口、Socket等通信资源。")
                ]),
            new HelpSection(
                "参数说明",
                "下列默认值适用于多数现场设备；调整后随会话保存，重新打开程序仍然生效。",
                [
                    new HelpEntry("响应超时", "默认 1000 ms。单次请求等待从站应答的最长时间，超过后按失败处理并计入超时统计。"),
                    new HelpEntry("重试次数", "默认 2 次。超时或通信错误后的重试上限，重试仍失败则该轮询点标记为错误。"),
                    new HelpEntry("重试间隔", "默认 50 ms。两次重试之间的等待时间，现场干扰较大时可适当加大。"),
                    new HelpEntry("重连间隔", "默认 1000 ms。连接中断后自动重连的等待时间。"),
                    new HelpEntry("报文间隔", "默认 10 ms。相邻两帧之间的最小发送间隔，避免从站处理不过来导致丢帧。"),
                    new HelpEntry("队列容量", "默认 1000 条。待发送命令队列上限；队列满时优先丢弃轮询命令，手动命令优先发送。"),
                    new HelpEntry("轮询周期", "每个读取块独立配置的轮询间隔，与超时、重试共同决定单轮采集耗时。"),
                    new HelpEntry("写入锁", "从写入帧发出到收到应答期间锁定该点位显示，避免轮询数据覆盖用户输入；超时或异常时解锁并提示写入失败。")
                ])
        ];
    }

    private static IReadOnlyList<HelpSection> BuildSlaveSections()
    {
        return
        [
            new HelpSection(
                "基础概述",
                "ModbusSlave 是 Modbus TCP / RTU 从站仿真器，用于在没有真实设备的情况下为主站提供数据源，也可注入故障验证主站的异常处理。顶部折叠分组配置监听参数，下方标签页管理寄存器、仿真与干扰。",
                []),
            new HelpSection(
                "功能说明",
                string.Empty,
                [
                    new HelpEntry("从站仿真", "顶部折叠分组配置站号、TCP/RTU监听参数、数据持久化开关；下方标签页分区管理四区寄存器、字符串、仿真发生器、干扰报文、故障注入。"),
                    new HelpEntry("寄存器仿真", "支持固定值、自增、随机值仿真，可设置仿真周期、数值上下限；支持寄存器批量修改、批量导入导出。"),
                    new HelpEntry("故障注入", "支持模拟丢包、CRC错误、异常响应、延时报文，用于验证主站异常处理逻辑。"),
                    new HelpEntry("报文监视器", "支持按方向、站号、功能码、关键字筛选报文；CRC错误标红、异常响应标橙。"),
                    new HelpEntry("日志查看", "按日志级别、分类、关键字筛选，支持日志导出，记录通信耗时、异常详情。"),
                    new HelpEntry("数据安全", "所有站号的数据区相互独立，程序退出时自动原子保存，避免寄存器数据损坏。")
                ]),
            new HelpSection(
                "参数说明",
                "监听参数修改后需要重新启动监听才会生效；仿真与故障注入参数可在线调整。",
                [
                    new HelpEntry("站号", "从站地址，取值 1–247；主站请求的站号必须与之匹配，否则不应答。"),
                    new HelpEntry("监听端口", "默认 502。Modbus TCP 标准端口；本机联调无管理员权限时可改用 1502 等非保留端口。"),
                    new HelpEntry("波特率", "默认 9600。RTU 串口速率，下拉预置常用值，也可输入非标波特率。"),
                    new HelpEntry("数据位", "默认 8 位，Modbus RTU 通常固定为 8 数据位、1 停止位。"),
                    new HelpEntry("寄存器显示条数", "默认 100 行。仅影响表格一次展示的行数，不会改变实际数据区大小。"),
                    new HelpEntry("仿真周期", "仿真发生器的刷新间隔，决定自增与随机值的更新频率。"),
                    new HelpEntry("故障注入参数", "丢包率、CRC 错误、异常响应码与延时时长，用于构造主站需要处理的异常场景。")
                ])
        ];
    }

    private static string NormalizeVersion(string? informationalVersion, Version? assemblyVersion)
    {
        string value = informationalVersion ?? assemblyVersion?.ToString() ?? "1.0.0";
        int metadataIndex = value.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex > 0 ? value[..metadataIndex] : value;
    }
}
