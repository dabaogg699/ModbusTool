// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO;

namespace ModbusTool.App.Services;

/// <summary>
/// 用户数据统一存放于 %APPDATA%\ModbusTool（Roaming），程序安装目录全程只读。
/// 程序本体可能位于 Program Files 等受保护目录，任何写入（模板、工作区、运行缓存、日志）
/// 都必须通过本类获取路径，禁止再使用 AppContext.BaseDirectory 写文件。
/// </summary>
public static class AppDataPathHelper
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ModbusTool");
        return root;
    });

    /// <summary>用户数据根目录：%APPDATA%\ModbusTool。</summary>
    public static string Root => RootLazy.Value;

    /// <summary>用户模板目录（内置默认模板不在此处，随 exe 只读分发）。</summary>
    public static string TemplatesFolder => Path.Combine(Root, "templates");

    /// <summary>工作区目录：last.mbwork 与用户另存的 .mbwork 默认落在此处。</summary>
    public static string WorkspacesFolder => Path.Combine(Root, "workspaces");

    /// <summary>运行时缓存：slave-config.json、data.bin、临时 .tmp 文件。</summary>
    public static string RuntimeCacheFolder => Path.Combine(Root, "runtime-cache");

    /// <summary>运行日志与报文落盘目录。</summary>
    public static string LogsFolder => Path.Combine(Root, "logs");

    /// <summary>上次退出时的工作区快照。</summary>
    public static string LastWorkspaceFile => Path.Combine(WorkspacesFolder, "last.mbwork");

    /// <summary>
    /// 启动时调用：递归创建全部用户数据目录。任一目录创建失败返回 false，
    /// 调用方应弹窗提示（例如 %APPDATA% 被策略锁定时引导用户更换环境）。
    /// </summary>
    public static bool EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(TemplatesFolder);
            Directory.CreateDirectory(WorkspacesFolder);
            Directory.CreateDirectory(RuntimeCacheFolder);
            Directory.CreateDirectory(LogsFolder);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
