// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ByteConvert;

namespace ModbusSlave;

public sealed class SlaveConfiguration
{
    public int SchemaVersion { get; set; } = 1;

    public List<SlaveStationConfiguration> Stations { get; set; } = [new SlaveStationConfiguration()];

    public bool UseTcp { get; set; } = true;

    public bool UseSerial { get; set; }

    // 默认监听本机环回；用户可在下拉框手动切回 0.0.0.0 全网卡监听。
    public string TcpAddress { get; set; } = "127.0.0.1";

    public int TcpPort { get; set; } = 502;

    public string PortName { get; set; } = "COM1";

    public int BaudRate { get; set; } = 9600;

    public int DataBits { get; set; } = 8;

    public string Parity { get; set; } = "None";

    public string StopBits { get; set; } = "One";

    public string SelectedEncoding { get; set; } = "GB2312";

    public bool SwapStringBytes { get; set; }

    public int GridStartAddress { get; set; }

    public int GridDisplayCount { get; set; } = 100;

    public ModbusDataType TableDataType { get; set; } = ModbusDataType.UInt16;

    public bool TableDisplayHex { get; set; }

    public ModbusByteOrder TableByteOrder { get; set; } = ModbusByteOrder.Order1032;
}

public sealed class SlaveStationConfiguration
{
    public byte UnitId { get; set; } = 1;

    public string DataFilePath { get; set; } = "data.bin";
}
