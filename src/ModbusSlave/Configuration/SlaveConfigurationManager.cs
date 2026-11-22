// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModbusCore.ByteConvert;

namespace ModbusSlave;

public sealed class SlaveConfigurationManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SlaveConfiguration Current { get; private set; } = new();

    public string? FilePath { get; private set; }

    public bool IsDirty { get; private set; } = true;

    public bool LegacyImported { get; private set; }

    public IReadOnlyList<string> LegacyCommunicationHistory { get; private set; } = [];

    public void MarkDirty() => IsDirty = true;

    public async ValueTask LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);
        await using FileStream stream = File.OpenRead(fullPath);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (LegacySlaveConfigMigrator.IsLegacy(document.RootElement))
        {
            string baseDirectory = Path.GetDirectoryName(fullPath)
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ModbusTool",
                    "runtime-cache");
            LegacySlaveConfiguration legacy = LegacySlaveConfigMigrator.Migrate(document.RootElement, baseDirectory);
            Validate(legacy.Configuration);
            Current = legacy.Configuration;
            LegacyCommunicationHistory = legacy.CommunicationHistory;
            LegacyImported = true;
            FilePath = null;
            IsDirty = true;
        }
        else
        {
            SlaveConfiguration configuration = document.RootElement.Deserialize<SlaveConfiguration>(SerializerOptions)
                ?? throw new InvalidDataException("从站配置文件为空。");
            Validate(configuration);
            Current = configuration;
            LegacyCommunicationHistory = [];
            LegacyImported = false;
            FilePath = fullPath;
            IsDirty = false;
        }
    }

    public async ValueTask SaveAsync(string? filePath = null, CancellationToken cancellationToken = default)
    {
        string? targetPath = filePath ?? FilePath;
        ArgumentGuard.ThrowIfNullOrWhiteSpace(targetPath);
        Validate(Current);
        string fullPath = Path.GetFullPath(targetPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, Current, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        FilePath = fullPath;
        IsDirty = false;
        LegacyImported = false;
    }

    private static void Validate(SlaveConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion != 1)
        {
            throw new NotSupportedException($"不支持的从站配置版本 {configuration.SchemaVersion}。");
        }

        if (configuration.Stations.Count == 0
            || configuration.Stations.Any(station => station.UnitId == 0 || string.IsNullOrWhiteSpace(station.DataFilePath))
            || configuration.Stations.Select(station => station.UnitId).Distinct().Count() != configuration.Stations.Count)
        {
            throw new InvalidDataException("从站站号必须非零、不能重复，且每个站号必须配置数据文件。");
        }

        if (!IPAddress.TryParse(configuration.TcpAddress, out _)
            || configuration.TcpPort is < 0 or > 65535
            || string.IsNullOrWhiteSpace(configuration.PortName)
            || configuration.BaudRate <= 0
            || configuration.DataBits is < 5 or > 8)
        {
            throw new InvalidDataException("从站 TCP 或串口配置无效。");
        }

        string[] parities = ["None", "Even", "Odd", "Mark", "Space"];
        string[] stopBits = ["None", "One", "OnePointFive", "Two"];
        string[] encodings = ["GB2312", "UTF-8", "Unicode", "ASCII"];
        if (!parities.Contains(configuration.Parity, StringComparer.OrdinalIgnoreCase)
            || !stopBits.Contains(configuration.StopBits, StringComparer.OrdinalIgnoreCase)
            || !encodings.Contains(configuration.SelectedEncoding, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("从站校验位、停止位或字符串编码配置无效。");
        }

        if (configuration.GridStartAddress is < 0 or > 65535
            || configuration.GridDisplayCount < 1
            || configuration.GridStartAddress + configuration.GridDisplayCount > 65536
            || !Enum.IsDefined(configuration.TableDataType)
            || !Enum.IsDefined(configuration.TableByteOrder))
        {
            throw new InvalidDataException("从站表格范围、数据类型或字节序配置无效。");
        }
    }
}
