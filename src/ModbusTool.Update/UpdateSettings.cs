// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Text.Json;

namespace ModbusTool.Update;

public sealed class UpdateSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public int SchemaVersion { get; set; } = 1;
    public string ManifestUrl { get; set; } = string.Empty;
    public bool AutoCheck { get; set; }
    public bool Silent { get; set; } = true;
    public long MaximumPackageBytes { get; set; } = 512 * 1024 * 1024;
    public long MaximumExtractedBytes { get; set; } = 1024 * 1024 * 1024;

    public static async Task<UpdateSettings> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            return new UpdateSettings();
        }

        string json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        UpdateSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<UpdateSettings>(json, SerializerOptions)
                ?? throw new InvalidDataException("更新设置为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("更新设置 JSON 格式无效。", exception);
        }

        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException($"不支持的更新设置版本 {SchemaVersion}。");
        }

        if (!string.IsNullOrWhiteSpace(ManifestUrl))
        {
            UpdateUri.ValidateHttps(ManifestUrl, "更新清单地址");
        }

        if (MaximumPackageBytes <= 0 || MaximumExtractedBytes <= 0
            || MaximumExtractedBytes < MaximumPackageBytes)
        {
            throw new InvalidDataException("更新下载和解压大小限制无效。");
        }
    }
}
