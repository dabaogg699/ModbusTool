// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Text.Json;

namespace ModbusTool.Update;

public sealed class UpdateManifest
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public Dictionary<string, UpdatePackage> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static UpdateManifest Parse(string json)
    {
        UpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, SerializerOptions)
                ?? throw new InvalidDataException("更新清单为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("更新清单 JSON 格式无效。", exception);
        }

        manifest.Validate();
        return manifest;
    }

    public Version GetVersion() => System.Version.Parse(Version);

    public UpdatePackage GetPackage(string runtimeIdentifier)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        if (!Packages.TryGetValue(runtimeIdentifier, out UpdatePackage? package))
        {
            throw new InvalidDataException($"更新清单不包含当前平台 {runtimeIdentifier} 的安装包。");
        }

        package.Validate();
        return package;
    }

    private void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException($"不支持的更新清单版本 {SchemaVersion}。");
        }

        if (!System.Version.TryParse(Version, out Version? version) || version < new Version(0, 0))
        {
            throw new InvalidDataException("更新清单中的版本号无效，必须使用例如 1.0.1 的数字版本。");
        }

        if (Packages is null || Packages.Count == 0)
        {
            throw new InvalidDataException("更新清单未包含任何安装包。");
        }

        foreach ((string runtimeIdentifier, UpdatePackage package) in Packages)
        {
            if (string.IsNullOrWhiteSpace(runtimeIdentifier) || package is null)
            {
                throw new InvalidDataException("更新清单包含无效的平台或安装包。");
            }

            package.Validate();
        }
    }
}

public sealed class UpdatePackage
{
    public string Url { get; set; } = string.Empty;
    public string Md5 { get; set; } = string.Empty;
    public long Size { get; set; }

    public void Validate()
    {
        UpdateUri.ValidateHttps(Url, "安装包地址");
        if (Md5 is null || Md5.Length != 32 || !Md5.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("安装包 MD5 必须是 32 位十六进制文本。");
        }

        if (Size <= 0)
        {
            throw new InvalidDataException("安装包大小必须大于零。");
        }
    }
}

public static class UpdateUri
{
    public static Uri ValidateHttps(string value, string fieldName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidDataException($"{fieldName}必须是绝对 HTTPS 地址。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException($"{fieldName}不能包含用户名或密码。");
        }

        return uri;
    }
}
