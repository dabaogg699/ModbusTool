// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ByteConvert;
using ModbusCore.ModbusProtocol;

namespace ModbusMaster;

public enum MasterConnectionKind
{
    SerialRtu,
    Tcp
}

public sealed class MasterWorkspace
{
    public int SchemaVersion { get; set; } = 1;

    public string Name { get; set; } = "新建工作区";

    public MasterConnectionSettings Connection { get; set; } = new();

    public List<MasterDevice> Devices { get; set; } = [];

    public List<MasterPointGroup> PointGroups { get; set; } = [];

    /// <summary>趋势图每条曲线的自定义样式，按点位 Id 关联，随工作区保存与加载。</summary>
    public List<TrendSeriesStyle> TrendSeriesStyles { get; set; } = [];

    public override string ToString() => Name;
}

public sealed class TrendSeriesStyle
{
    public Guid PointId { get; set; }

    public string Color { get; set; } = string.Empty;

    public double LineWidth { get; set; } = 1.7;

    public string LinePattern { get; set; } = "Solid";
}

public sealed class MasterPointGroup
{
    public string Name { get; set; } = "默认分组";

    public bool IsExpanded { get; set; } = true;
}

public sealed class MasterConnectionSettings
{
    public MasterConnectionKind Kind { get; set; }

    public string PortName { get; set; } = "COM1";

    public int BaudRate { get; set; } = 9600;

    public int DataBits { get; set; } = 8;

    public string Parity { get; set; } = "None";

    public string StopBits { get; set; } = "One";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 502;

    public int ResponseTimeoutMs { get; set; } = 1000;

    public int RetryCount { get; set; } = 2;

    public int RetryDelayMs { get; set; } = 50;

    public int ReconnectDelayMs { get; set; } = 1000;

    public int FrameIntervalMs { get; set; } = 10;

    public int QueueCapacity { get; set; } = 1000;
}

public sealed class MasterDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "从站";

    public byte UnitId { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public int PollIntervalMs { get; set; } = 1000;

    public int PollOffsetMs { get; set; }

    public List<MasterReadBlock> ReadBlocks { get; set; } = [];
}

public sealed class MasterReadBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "读取块";

    public ModbusFunction Function { get; set; } = ModbusFunction.ReadHoldingRegisters;

    public ushort StartAddress { get; set; }

    public ushort Quantity { get; set; } = 1;

    public List<MasterDataPoint> Points { get; set; } = [];
}

public sealed class MasterDataPoint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "点位";

    public ushort AddressOffset { get; set; }

    public ModbusDataType DataType { get; set; } = ModbusDataType.UInt16;

    public ModbusByteOrder ByteOrder { get; set; } = ModbusByteOrder.Order1032;

    public double Scale { get; set; } = 1;

    public double Offset { get; set; }

    public string Unit { get; set; } = string.Empty;

    public bool HistoryEnabled { get; set; }

    public string GroupName { get; set; } = "默认分组";

    public string Note { get; set; } = string.Empty;
}

public sealed class PollSchedule
{
    public required Guid DeviceId { get; init; }

    public required Guid BlockId { get; init; }

    public required byte UnitId { get; init; }

    public required ModbusRequest Request { get; init; }

    public required TimeSpan Interval { get; init; }

    public TimeSpan StartDelay { get; init; }

    public MasterReadBlock? Block { get; init; }

    public TimeSpan? ResponseTimeout { get; init; }

    public int? RetryCount { get; init; }
}
