// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO;
using ModbusCore.Common;

namespace ModbusTool.App;

/// <summary>
/// 共享日志入口。独立主站/从站入口均通过该静态实例写入同一格式的运行日志。
/// </summary>
public static class App
{
    public static RollingFileLogService Log { get; } = new(ModbusTool.App.Services.AppDataPathHelper.LogsFolder);
}
