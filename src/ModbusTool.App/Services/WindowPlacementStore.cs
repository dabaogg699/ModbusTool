// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO;
using System.Text.Json;
using ModbusCore.Common;

namespace ModbusTool.App.Services;

/// <summary>
/// 主窗口位置、大小与左右面板折叠状态，保存到用户本地应用数据目录。
/// </summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>当前左侧导航页索引。</summary>
    public int NavigationIndex { get; set; }

    /// <summary>各分组 Expander 的展开状态。</summary>
    public Dictionary<string, bool> Sections { get; set; } = [];

    /// <summary>DataGrid 列宽（像素）。</summary>
    public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; set; } = [];
}

public static class WindowPlacementStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string ResolvePath(string product)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModbusTool");
        return Path.Combine(directory, $"{product}.window.json");
    }

    public static WindowPlacement? Load(string product)
    {
        try
        {
            string path = ResolvePath(product);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path), SerializerOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            App.Log.Write(ToolLogLevel.Warning, "界面", $"窗口状态读取失败，已使用默认布局（{product}）", exception);
            return null;
        }
    }

    public static void Save(string product, WindowPlacement placement)
    {
        try
        {
            string path = ResolvePath(product);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(placement, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            App.Log.Write(ToolLogLevel.Warning, "界面", $"窗口状态保存失败（{product}）", exception);
        }
    }
}
