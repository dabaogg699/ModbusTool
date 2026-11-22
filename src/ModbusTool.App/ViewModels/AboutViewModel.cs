// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace ModbusTool.App.ViewModels;

/// <summary>
/// 【关于】页面数据源：自动读取入口程序集版本与构建时间，展示固定的作者、功能简介与自研依赖说明。
/// G11 起界面不再读取 about.json；该文件仅保留在发布目录供本地自行维护，缺失不会影响关于页。
/// </summary>
public sealed class AboutViewModel
{
    private const string FixedAuthor = "工控菜鸡刘";
    private const string FixedEmail = "2011049450@qq.com";

    // 在线文档链接集中在此处维护：更换仓库或文档路径时只改这两个常量，XAML 无需调整。
    private const string RepositoryBaseUrl = "https://github.com/dabaogg699/ModbusTool";
    private const string HelpDocumentDirectory = "blob/main/docs/help";
    private const string DebugDocumentFile = "DebugGuide.md";

    public AboutViewModel(string productName, string productKind)
    {
        ProductName = string.IsNullOrWhiteSpace(productName) ? "ModbusTool" : productName.Trim();
        ProductKind = string.IsNullOrWhiteSpace(productKind) ? "主站" : productKind.Trim();
        Assembly entry = Assembly.GetEntryAssembly() ?? typeof(AboutViewModel).Assembly;
        AssemblyName assemblyName = entry.GetName();
        Version = NormalizeVersion(
            entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assemblyName.Version);
        BuildTime = ResolveBuildTime(entry);
        Runtime = $".NET {Environment.Version}";
        BuildSdk = ResolveMetadata(entry, "BuildSdk", "-");
        string processFileName = Path.GetFileName(Environment.ProcessPath) ?? string.Empty;
        FileName = string.Equals(Path.GetFileNameWithoutExtension(processFileName), ProductName, StringComparison.OrdinalIgnoreCase)
            ? processFileName
            : $"{ProductName}.exe";

        Author = FixedAuthor;
        AuthorLine = $"作者：{FixedAuthor}";
        EmailLine = $"Email：{FixedEmail}";
        Description = string.Equals(ProductKind, "从站", StringComparison.Ordinal)
            ? "ModbusSlave 独立从站仿真程序，支持 Modbus TCP / RTU 监听、数据区编辑与故障注入。"
            : "ModbusMaster 独立主站调试程序，支持多会话轮询、点位管理、趋势与实时报文监视。";
        FeatureSummary =
            "ModbusTool是面向工业现场自研的Modbus调试套件，分为ModbusMaster主站、ModbusSlave从站模拟器，支持Modbus TCP、Modbus RTU串口通信，可替代Modbus Poll / Modbus Slave，用于PLC、变频器、仪表、智能模块协议调试、仿真、压力测试与异常报文验证。支持多Tab独立会话并行调试、CSV点位表导入导出、实时趋势曲线、报文染色监视、故障注入、分级日志导出、寄存器持久化，内置4套工业主题。";
        Copyright = "©2026 工控菜鸡刘，开源协议：MIT License";
        LicenseNotice = "开源协议：MIT License（详见仓库LICENSE）";
        RepositoryUrl = $"仓库地址：{RepositoryBaseUrl}";
        HelpDocumentUrl = $"{RepositoryBaseUrl}/{HelpDocumentDirectory}/{ProductName}-help.md";
        DebugDocumentUrl = $"{RepositoryBaseUrl}/{HelpDocumentDirectory}/{DebugDocumentFile}";
        OpenHelpDocumentCommand = new RelayCommand(_ => OpenUrl(HelpDocumentUrl));
        OpenDebugDocumentCommand = new RelayCommand(_ => OpenUrl(DebugDocumentUrl));
        SelfDevelopedNote =
            "协议帧编解码、RTU/TCP传输、CRC16校验、主站调度与从站服务均为自研实现，未使用NModbus等第三方协议栈。";
        ModbusCoreNote =
            "依赖自研ModbusCore类库，统一处理功能码、异常码、帧解析、CRC校验、数据区模型与传输层，主站与从站共用同一套协议底层，保证行为一致。";
    }

    public string ProductName { get; }

    public string ProductKind { get; }

    public string Version { get; }

    public string BuildTime { get; }

    public string Runtime { get; }

    public string BuildSdk { get; }

    public string FileName { get; }

    public string Author { get; }

    public string AuthorLine { get; }

    public string EmailLine { get; }

    public string Description { get; }

    public string FeatureSummary { get; }

    public string Copyright { get; }

    public string SelfDevelopedNote { get; }

    public string ModbusCoreNote { get; }

    public string LicenseNotice { get; }

    public string RepositoryUrl { get; }

    /// <summary>帮助文档在线地址，由 <see cref="ProductName"/> 拼出主站或从站文档路径。</summary>
    public string HelpDocumentUrl { get; }

    /// <summary>调试辅助文档在线地址。</summary>
    public string DebugDocumentUrl { get; }

    public ICommand OpenHelpDocumentCommand { get; }

    public ICommand OpenDebugDocumentCommand { get; }

    public string WindowTitle => $"关于 {ProductName}";

    /// <summary>使用系统默认浏览器打开在线文档；失败时提示，避免链接点击无响应。</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show($"无法打开浏览器，请手动访问：{url}\n{exception.Message}", "打开文档", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string NormalizeVersion(string? informationalVersion, Version? assemblyVersion)
    {
        string value = informationalVersion ?? assemblyVersion?.ToString() ?? "1.0.0";
        int metadataIndex = value.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex > 0 ? value[..metadataIndex] : value;
    }

    private static string ResolveMetadata(Assembly assembly, string key, string fallback)
    {
        string? value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string ResolveBuildTime(Assembly assembly)
    {
        string? metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "BuildTime", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            return metadata;
        }

        try
        {
            // 单文件发布下 Assembly.Location 为空，改用宿主 exe 的时间戳作为兜底。
            string? processPath = Environment.ProcessPath;
            return string.IsNullOrEmpty(processPath)
                ? "未知"
                : File.GetLastWriteTime(processPath).ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "未知";
        }
    }
}
