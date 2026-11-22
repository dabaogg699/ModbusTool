// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace ModbusTool.App.Themes;

public enum AppTheme
{
    Light,
    Dark,
    IndustrialBlue,
    IndustrialDark,
    Custom
}

public sealed record ThemeOption(AppTheme Theme, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class ThemeDefinition
{
    public string Name { get; set; } = "自定义主题";

    public bool IsDark { get; set; }

    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ThemeManager
{
    public static readonly IReadOnlyList<ThemeOption> BuiltInThemes =
    [
        new(AppTheme.Light, "浅色"),
        new(AppTheme.Dark, "深色"),
        new(AppTheme.IndustrialBlue, "工业蓝"),
        new(AppTheme.IndustrialDark, "深灰工业")
    ];

    public static readonly IReadOnlyList<string> ColorKeys =
    [
        "WindowBackgroundColor", "PanelColor", "PanelAltColor", "BorderColor", "TextColor",
        "MutedTextColor", "AccentColor", "AccentHoverColor", "SuccessColor", "ErrorColor",
        "InputBackgroundColor", "SelectionColor", "StatusFreshColor", "StatusStaleColor",
        "StatusErrorColor", "StatusUnknownColor", "StatusFreshBackgroundColor",
        "StatusStaleBackgroundColor", "StatusErrorBackgroundColor", "StatusUnknownBackgroundColor",
        "PacketSentColor", "PacketReceivedColor", "PacketErrorColor", "PacketExceptionColor", "PacketCrcMalformedColor"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static ResourceDictionary? _customDictionary;

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Light;

    public static bool IsDark { get; private set; }

    public static string CurrentThemeName { get; private set; } = "浅色";

    public static event EventHandler? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        if (theme == AppTheme.Custom || !Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme), "请使用内置主题或导入自定义主题。");
        }

        string assemblyName = typeof(ThemeManager).Assembly.GetName().Name
            ?? throw new InvalidOperationException("无法获取主题程序集名称。");
        ResourceDictionary themeDictionary = new()
        {
            Source = new Uri($"/{assemblyName};component/Themes/{theme}.xaml", UriKind.Relative)
        };
        Collection<ResourceDictionary> dictionaries = RequireDictionaries();
        RemoveCustom(dictionaries);
        for (int index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (IsBuiltInThemeDictionary(dictionaries[index]))
            {
                dictionaries.RemoveAt(index);
            }
        }

        dictionaries.Add(themeDictionary);
        CurrentTheme = theme;
        IsDark = theme is AppTheme.Dark or AppTheme.IndustrialDark;
        CurrentThemeName = BuiltInThemes.FirstOrDefault(option => option.Theme == theme)?.DisplayName ?? theme.ToString();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyCustom(ThemeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Colors is null || definition.Colors.Count == 0)
        {
            throw new InvalidDataException("主题文件未包含任何颜色定义。");
        }

        Collection<ResourceDictionary> dictionaries = RequireDictionaries();
        if (!dictionaries.Any(IsBuiltInThemeDictionary))
        {
            Apply(AppTheme.Light);
        }

        ResourceDictionary custom = [];
        foreach ((string key, string value) in definition.Colors)
        {
            string colorKey = ResolveColorKey(key);
            Color color = ParseColor(value, key);
            string brushKey = colorKey[..^"Color".Length] + "Brush";
            custom[colorKey] = color;
            custom[brushKey] = new SolidColorBrush(color);
        }

        RemoveCustom(dictionaries);
        int themeIndex = dictionaries.ToList().FindLastIndex(IsBuiltInThemeDictionary);
        dictionaries.Insert(themeIndex >= 0 ? themeIndex + 1 : dictionaries.Count, custom);
        _customDictionary = custom;
        CurrentTheme = AppTheme.Custom;
        IsDark = definition.IsDark || GetLuminance(GetColor("WindowBackgroundColor")) < 0.5;
        CurrentThemeName = string.IsNullOrWhiteSpace(definition.Name) ? "自定义主题" : definition.Name.Trim();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static ThemeDefinition ExportCurrent()
    {
        ThemeDefinition definition = new()
        {
            Name = CurrentThemeName,
            IsDark = IsDark
        };
        foreach (string key in ColorKeys)
        {
            definition.Colors[key] = ToHex(GetColor(key));
        }

        return definition;
    }

    public static async Task ExportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePath);
        ThemeDefinition definition = ExportCurrent();
        await using FileStream stream = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, definition, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<ThemeDefinition> ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePath);
        ThemeDefinition definition;
        try
        {
            await using FileStream stream = File.OpenRead(filePath);
            definition = await JsonSerializer.DeserializeAsync<ThemeDefinition>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("主题文件内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("主题文件不是有效的 JSON。", exception);
        }

        if (definition.Colors is null)
        {
            throw new InvalidDataException("主题文件缺少 Colors 节点。");
        }

        List<string> unknownKeys = definition.Colors.Keys
            .Where(key => !IsKnownKey(key))
            .ToList();
        if (unknownKeys.Count > 0)
        {
            throw new InvalidDataException($"主题文件包含未知颜色项：{string.Join("、", unknownKeys)}");
        }

        return definition;
    }

    private static bool IsKnownKey(string key)
    {
        string colorKey = ResolveColorKey(key);
        return ColorKeys.Contains(colorKey, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveColorKey(string key)
    {
        string trimmed = key.Trim();
        if (trimmed.EndsWith("Brush", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^"Brush".Length] + "Color";
        }

        return trimmed.EndsWith("Color", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "Color";
    }

    private static Color ParseColor(string value, string key)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            throw new InvalidDataException($"颜色项 {key} 的值“{value}”无效，请使用 #RRGGBB 或 #AARRGGBB。", exception);
        }
    }

    private static Color GetColor(string colorKey)
    {
        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
        {
            return Colors.Transparent;
        }

        if (resources[colorKey] is Color color)
        {
            return color;
        }

        string brushKey = colorKey[..^"Color".Length] + "Brush";
        return resources[brushKey] is SolidColorBrush brush ? brush.Color : Colors.Transparent;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static double GetLuminance(Color color) =>
        0.2126 * color.R / 255d + 0.7152 * color.G / 255d + 0.0722 * color.B / 255d;

    private static Collection<ResourceDictionary> RequireDictionaries()
    {
        return Application.Current?.Resources.MergedDictionaries
            ?? throw new InvalidOperationException("应用资源尚未初始化，无法切换主题。");
    }

    private static bool IsBuiltInThemeDictionary(ResourceDictionary dictionary)
    {
        string? source = dictionary.Source?.OriginalString;
        return source is not null && BuiltInThemes.Any(option =>
            source.Contains($"Themes/{option.Theme}.xaml", StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveCustom(Collection<ResourceDictionary> dictionaries)
    {
        if (_customDictionary is not null)
        {
            dictionaries.Remove(_customDictionary);
            _customDictionary = null;
            return;
        }

        for (int index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (dictionaries[index].Source is null && dictionaries[index].Contains("WindowBackgroundColor"))
            {
                dictionaries.RemoveAt(index);
            }
        }
    }
}
