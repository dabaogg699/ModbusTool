// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Globalization;
using System.Text.Json;
using ModbusCore.ByteConvert;

namespace ModbusSlave;

public sealed class LegacySlaveConfiguration
{
    public required SlaveConfiguration Configuration { get; init; }

    public IReadOnlyList<string> CommunicationHistory { get; init; } = [];
}

public static class LegacySlaveConfigMigrator
{
    public static bool IsLegacy(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty(nameof(SlaveConfiguration.SchemaVersion), out _))
        {
            return false;
        }

        return root.TryGetProperty("commstation", out _)
            || root.TryGetProperty("lastdbfile", out _)
            || root.TryGetProperty("regsmallendian", out _)
            || root.TryGetProperty("tbmode", out _);
    }

    public static LegacySlaveConfiguration Migrate(JsonElement root, string baseDirectory)
    {
        if (!IsLegacy(root))
        {
            throw new InvalidDataException("这不是旧版 MBSlave config.txt 配置。");
        }

        ArgumentGuard.ThrowIfNullOrWhiteSpace(baseDirectory);
        byte unitId = (byte)Math.Clamp(ReadInt(root, "commstation", 1), 1, byte.MaxValue);
        string dataFile = ReadString(root, "lastdbfile", "data.bin");
        if (string.IsNullOrWhiteSpace(dataFile))
        {
            dataFile = "data.bin";
        }

        string dataFilePath = Path.GetFullPath(dataFile, baseDirectory);
        int tableMode = Math.Clamp(ReadInt(root, "tbmode", 0), 0, 6);
        (ModbusDataType dataType, bool displayHex) = tableMode switch
        {
            0 => (ModbusDataType.UInt16, false),
            1 => (ModbusDataType.UInt16, true),
            2 => (ModbusDataType.UInt32, false),
            3 => (ModbusDataType.Float, false),
            4 => (ModbusDataType.Int16, false),
            5 => (ModbusDataType.Double, false),
            6 => (ModbusDataType.UInt32, true),
            _ => throw new InvalidDataException("旧版表格数据类型无效。")
        };
        bool smallEndian = ReadBool(root, "regsmallendian", true);
        SlaveConfiguration configuration = new()
        {
            Stations = [new SlaveStationConfiguration { UnitId = unitId, DataFilePath = dataFilePath }],
            UseTcp = false,
            UseSerial = true,
            Parity = "Even",
            SelectedEncoding = ReadEncoding(root),
            GridStartAddress = Math.Clamp(ReadInt(root, "tboffset", 0), 0, 65535),
            TableDataType = dataType,
            TableDisplayHex = displayHex,
            TableByteOrder = smallEndian ? ModbusByteOrder.Order2301 : ModbusByteOrder.Order0123
        };
        IReadOnlyList<string> history = ReadCommunicationHistory(root);
        ApplyFirstValidConnection(configuration, history);
        return new LegacySlaveConfiguration { Configuration = configuration, CommunicationHistory = history };
    }

    private static string ReadEncoding(JsonElement root)
    {
        string value = ReadString(root, "strencode", "GB2312").Trim().ToLowerInvariant();
        return value switch
        {
            "gb2312" or "gb" => "GB2312",
            "utf-8" or "utf8" => "UTF-8",
            "unicode" or "utf-16" => "Unicode",
            "ascii" => "ASCII",
            _ => throw new InvalidDataException($"旧版字符串编码“{value}”不受支持。")
        };
    }

    private static IReadOnlyList<string> ReadCommunicationHistory(JsonElement root)
    {
        if (!root.TryGetProperty("commhistory", out JsonElement element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static void ApplyFirstValidConnection(SlaveConfiguration configuration, IEnumerable<string> history)
    {
        foreach (string item in history)
        {
            string[] fields = item.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length == 1 && fields[0].StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(fields[0][4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tcpPort)
                && tcpPort is >= 1 and <= 65535)
            {
                configuration.UseTcp = true;
                configuration.UseSerial = false;
                configuration.TcpPort = tcpPort;
                return;
            }

            if (fields.Length != 5 || string.IsNullOrWhiteSpace(fields[0])
                || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int baudRate)
                || baudRate <= 0
                || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dataBits)
                || dataBits is < 5 or > 8)
            {
                continue;
            }

            string? parity = fields[2].ToUpperInvariant() switch
            {
                "N" => "None",
                "E" => "Even",
                "O" => "Odd",
                "M" => "Mark",
                "S" => "Space",
                _ => null
            };
            string? stopBits = fields[4] switch
            {
                "0" => "None",
                "1" => "One",
                "1.5" => "OnePointFive",
                "2" => "Two",
                _ => null
            };
            if (parity is null || stopBits is null)
            {
                continue;
            }

            configuration.PortName = fields[0];
            configuration.BaudRate = baudRate;
            configuration.DataBits = dataBits;
            configuration.Parity = parity;
            configuration.StopBits = stopBits;
            return;
        }
    }

    private static int ReadInt(JsonElement root, string name, int defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out int value) => value,
            JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int value) => value,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => defaultValue
        };
    }

    private static bool ReadBool(JsonElement root, string name, bool defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt32(out int value) => value != 0,
            JsonValueKind.String when bool.TryParse(element.GetString(), out bool value) => value,
            JsonValueKind.String when int.TryParse(element.GetString(), out int value) => value != 0,
            _ => defaultValue
        };
    }

    private static string ReadString(JsonElement root, string name, string defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? defaultValue,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => defaultValue
        };
    }
}
