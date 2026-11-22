// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ModbusCore.Common;

namespace ModbusTool.App;

/// <summary>
/// 双入口程序共用的启动与退出流程：日志、全局异常兜底、命令行工作区参数解析。
/// </summary>
public static class AppHost
{
    public static void OnStartup(Application application, string productName, StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 用户数据目录（模板/工作区/运行缓存/日志）必须最先就绪；%APPDATA% 不可写时给出友好提示。
        if (!Services.AppDataPathHelper.EnsureCreated())
        {
            MessageBox.Show(
                $"用户数据目录（{Services.AppDataPathHelper.Root}）创建失败。\n\n"
                + "请检查当前用户对 APPDATA 目录的写入权限，或联系管理员。",
                "用户数据目录不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        App.Log.Write(
            ToolLogLevel.Information,
            "系统",
            $"{productName} 启动，版本 {GetEntryVersion()}，参数：{string.Join(' ', e.Args)}");
    }

    public static void OnExit(ExitEventArgs e)
    {
        App.Log.Write(ToolLogLevel.Information, "系统", $"程序退出，代码 {e.ApplicationExitCode}");
        App.Log.DisposeAsync().AsTask().GetAwaiter().GetResult();
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    /// <summary>
    /// 解析命令行中已存在的工作区文件路径；独立主站入口据此在启动时自动打开工作区。
    /// </summary>
    public static string? ResolveWorkspacePath(IEnumerable<string> arguments)
    {
        return arguments.FirstOrDefault(argument =>
            !argument.StartsWith('-') && File.Exists(argument));
    }

    public static Version GetEntryVersion()
    {
        return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        App.Log.Write(ToolLogLevel.Error, "系统", "UI 线程未处理异常", e.Exception);
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        App.Log.Write(ToolLogLevel.Error, "系统", "应用程序域未处理异常", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        App.Log.Write(ToolLogLevel.Error, "系统", "后台任务未观察异常", e.Exception);
    }
}
