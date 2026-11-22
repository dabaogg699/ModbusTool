// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Globalization;
using System.Text.Json;
using ModbusCore.ByteConvert;
using ModbusCore.ModbusProtocol;

namespace ModbusMaster;

public sealed class LegacyMasterConfiguration
{
    public required IReadOnlyList<MasterWorkspace> Workspaces { get; init; }

    public IReadOnlyList<string> CommunicationHistory { get; init; } = [];

    public int[] WindowRectangle { get; init; } = [100, 100, 675, 480];

    public int WriteBatchSize { get; init; }
}

public static class LegacyMasterConfigMigrator
{
    public static bool IsLegacy(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("workspaces", out JsonElement workspaces)
            && workspaces.ValueKind == JsonValueKind.Array;
    }

    public static async ValueTask<LegacyMasterConfiguration> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = File.OpenRead(filePath);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Migrate(document.RootElement);
    }

    public static LegacyMasterConfiguration Migrate(JsonElement root)
    {
        if (!IsLegacy(root))
        {
            throw new InvalidDataException("这不是旧版 MBScan config.txt 配置。");
        }

        int frameInterval = Math.Clamp(ReadInt(root, "mbinterval", 50), 50, 5000);
        int responseTimeout = Math.Clamp(ReadInt(root, "mbtimeout", 200), 1, 5000);
        int retryCount = Math.Clamp(ReadInt(root, "mbretrys", 3), 1, 255);
        int pollInterval = Math.Max(1, Math.Clamp(ReadInt(root, "queryinterval", 1000), 0, 10000));
        int writeBatchSize = Math.Clamp(ReadInt(root, "mbwritebunch", 70), 0, 125);
        IReadOnlyList<string> history = ReadCommunicationHistory(root);
        MasterConnectionSettings connection = CreateConnection(history);
        connection.FrameIntervalMs = frameInterval;
        connection.ResponseTimeoutMs = responseTimeout;
        connection.RetryCount = retryCount;

        JsonElement workspacesElement = root.GetProperty("workspaces");
        if (workspacesElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException("旧版 MBScan 配置中没有工作区。");
        }

        List<MasterWorkspace> workspaces = [];
        int index = 0;
        foreach (JsonElement workspaceElement in workspacesElement.EnumerateArray())
        {
            workspaces.Add(MigrateWorkspace(workspaceElement, index++, pollInterval, connection));
        }

        return new LegacyMasterConfiguration
        {
            Workspaces = workspaces,
            CommunicationHistory = history,
            WindowRectangle = ReadWindowRectangle(root),
            WriteBatchSize = writeBatchSize
        };
    }

    private static MasterWorkspace MigrateWorkspace(
        JsonElement element,
        int workspaceIndex,
        int pollInterval,
        MasterConnectionSettings connection)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < 3
            || element[1].ValueKind != JsonValueKind.Array || element[2].ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"旧工作区 {workspaceIndex + 1} 的结构无效。");
        }

        string legacyName = element[0].ValueKind == JsonValueKind.String
            ? element[0].GetString() ?? string.Empty
            : string.Empty;
        MasterWorkspace workspace = new()
        {
            Name = string.IsNullOrWhiteSpace(legacyName)
                ? workspaceIndex == 0 ? "当前工作区（旧配置）" : $"旧工作区 {workspaceIndex + 1}"
                : legacyName,
            Connection = CopyConnection(connection)
        };
        Dictionary<byte, MasterDevice> devices = [];

        int blockIndex = 0;
        foreach (JsonElement item in element[1].EnumerateArray())
        {
            string[] fields = GetFields(item, $"工作区 {workspaceIndex + 1} 普通点位 {blockIndex + 1}");
            string context = $"工作区 {workspaceIndex + 1} 普通点位 {blockIndex + 1}";
            byte unitId = ReadByte(fields[0], 1, $"{context} 站号");
            ushort address = ReadUInt16(fields[1], $"{context} 地址");
            ushort quantity = ReadUInt16(fields[2], $"{context} 寄存器数");
            ModbusDataType dataType = MapRegisterDataType(ReadInt32(fields[3], $"{context} 数据类型"), context);
            ModbusByteOrder byteOrder = ReadByteOrder(fields[4], context);
            _ = ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, address, quantity);

            MasterDevice device = GetOrAddDevice(workspace, devices, unitId, pollInterval);
            MasterReadBlock block = new()
            {
                Name = $"保持寄存器 {address}，数量 {quantity}",
                Function = ModbusFunction.ReadHoldingRegisters,
                StartAddress = address,
                Quantity = quantity
            };
            block.Points.Add(new MasterDataPoint
            {
                Name = $"{dataType} @ {address}",
                DataType = dataType,
                ByteOrder = byteOrder
            });
            device.ReadBlocks.Add(block);
            blockIndex++;
        }

        int historyIndex = 0;
        foreach (JsonElement item in element[2].EnumerateArray())
        {
            string[] fields = GetFields(item, $"工作区 {workspaceIndex + 1} 历史点 {historyIndex + 1}");
            string context = $"工作区 {workspaceIndex + 1} 历史点 {historyIndex + 1}";
            byte unitId = ReadByte(fields[0], 1, $"{context} 站号");
            ushort address = ReadUInt16(fields[1], $"{context} 地址");
            ModbusDataType dataType = MapHistoryDataType(ReadInt32(fields[2], $"{context} 数据类型"), context);
            ModbusByteOrder byteOrder = ReadByteOrder(fields[3], context);
            int registerCount = RegisterConverter.GetRegisterCount(dataType);
            if (address + registerCount > 65536)
            {
                throw new InvalidDataException($"{context} 超出寄存器地址范围。");
            }

            MasterDevice device = GetOrAddDevice(workspace, devices, unitId, pollInterval);
            MasterReadBlock? containingBlock = device.ReadBlocks.FirstOrDefault(block =>
                address >= block.StartAddress && address + registerCount <= block.StartAddress + block.Quantity);
            if (containingBlock is null)
            {
                containingBlock = new MasterReadBlock
                {
                    Name = $"历史点读取 {address}",
                    Function = ModbusFunction.ReadHoldingRegisters,
                    StartAddress = address,
                    Quantity = checked((ushort)registerCount)
                };
                device.ReadBlocks.Add(containingBlock);
            }

            containingBlock.Points.Add(new MasterDataPoint
            {
                Name = fields[4],
                AddressOffset = checked((ushort)(address - containingBlock.StartAddress)),
                DataType = dataType,
                ByteOrder = byteOrder,
                HistoryEnabled = true
            });
            historyIndex++;
        }

        return workspace;
    }

    private static MasterDevice GetOrAddDevice(
        MasterWorkspace workspace,
        Dictionary<byte, MasterDevice> devices,
        byte unitId,
        int pollInterval)
    {
        if (devices.TryGetValue(unitId, out MasterDevice? device))
        {
            return device;
        }

        device = new MasterDevice
        {
            Name = $"从站 {unitId}",
            UnitId = unitId,
            PollIntervalMs = pollInterval
        };
        devices.Add(unitId, device);
        workspace.Devices.Add(device);
        return device;
    }

    private static ModbusDataType MapRegisterDataType(int value, string context) => value switch
    {
        0 or 2 => ModbusDataType.UInt16,
        1 => ModbusDataType.Int16,
        3 or 5 => ModbusDataType.UInt32,
        4 => ModbusDataType.Int32,
        6 => ModbusDataType.Float,
        7 => ModbusDataType.Double,
        _ => throw new InvalidDataException($"{context} 的数据类型编号 {value} 无效。")
    };

    private static ModbusDataType MapHistoryDataType(int value, string context) => value switch
    {
        0 => ModbusDataType.UInt16,
        1 => ModbusDataType.Int16,
        2 => ModbusDataType.UInt32,
        3 => ModbusDataType.Int32,
        4 => ModbusDataType.Float,
        5 => ModbusDataType.Double,
        _ => throw new InvalidDataException($"{context} 的数据类型编号 {value} 无效。")
    };

    private static ModbusByteOrder ReadByteOrder(string value, string context)
    {
        int number = ReadInt32(value, $"{context} 字节序");
        if (number is < 0 or > 3)
        {
            throw new InvalidDataException($"{context} 的字节序编号 {number} 无效。");
        }

        return (ModbusByteOrder)number;
    }

    private static string[] GetFields(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{context} 不是字符串记录。");
        }

        string[] fields = (element.GetString() ?? string.Empty).Trim().Split(',');
        if (fields.Length < 5)
        {
            throw new InvalidDataException($"{context} 缺少字段，应至少包含 5 列。");
        }

        return fields;
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

    private static MasterConnectionSettings CreateConnection(IEnumerable<string> history)
    {
        MasterConnectionSettings settings = new();
        foreach (string item in history)
        {
            if (TryApplyConnection(settings, item))
            {
                break;
            }
        }

        return settings;
    }

    private static bool TryApplyConnection(MasterConnectionSettings settings, string value)
    {
        string[] fields = value.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length == 1 && TryParseTcpEndpoint(fields[0], out string? host, out int port))
        {
            settings.Kind = MasterConnectionKind.Tcp;
            settings.Host = host;
            settings.Port = port;
            return true;
        }

        if (fields.Length != 5 || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int baudRate)
            || baudRate <= 0 || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dataBits)
            || dataBits is < 5 or > 8)
        {
            return false;
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
        if (parity is null || stopBits is null || string.IsNullOrWhiteSpace(fields[0]))
        {
            return false;
        }

        settings.Kind = MasterConnectionKind.SerialRtu;
        settings.PortName = fields[0];
        settings.BaudRate = baudRate;
        settings.DataBits = dataBits;
        settings.Parity = parity;
        settings.StopBits = stopBits;
        return true;
    }

    private static bool TryParseTcpEndpoint(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        int separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1
            || !int.TryParse(value[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        host = value[..separator].Trim().Trim('[', ']');
        return host.Length > 0;
    }

    private static int[] ReadWindowRectangle(JsonElement root)
    {
        int[] result = [100, 100, 675, 480];
        if (!root.TryGetProperty("windowrect", out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return result;
        }

        string[] fields = (element.GetString() ?? string.Empty).Split(',');
        for (int index = 0; index < result.Length && index < fields.Length; index++)
        {
            if (int.TryParse(fields[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                result[index] = value;
            }
        }

        if (result[2] == 0) result[2] = 675;
        if (result[3] == 0) result[3] = 480;
        return result;
    }

    private static MasterConnectionSettings CopyConnection(MasterConnectionSettings source) => new()
    {
        Kind = source.Kind,
        PortName = source.PortName,
        BaudRate = source.BaudRate,
        DataBits = source.DataBits,
        Parity = source.Parity,
        StopBits = source.StopBits,
        Host = source.Host,
        Port = source.Port,
        ResponseTimeoutMs = source.ResponseTimeoutMs,
        RetryCount = source.RetryCount,
        RetryDelayMs = source.RetryDelayMs,
        ReconnectDelayMs = source.ReconnectDelayMs,
        FrameIntervalMs = source.FrameIntervalMs,
        QueueCapacity = source.QueueCapacity
    };

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
            _ => defaultValue
        };
    }

    private static int ReadInt32(string value, string context)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw new InvalidDataException($"{context} 不是有效整数。");
    }

    private static byte ReadByte(string value, byte minimum, string context)
    {
        int result = ReadInt32(value, context);
        return result >= minimum && result <= byte.MaxValue
            ? (byte)result
            : throw new InvalidDataException($"{context} 必须位于 {minimum}～255。");
    }

    private static ushort ReadUInt16(string value, string context)
    {
        int result = ReadInt32(value, context);
        return result is >= 0 and <= ushort.MaxValue
            ? (ushort)result
            : throw new InvalidDataException($"{context} 必须位于 0～65535。");
    }
}
