// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ByteConvert;
using ModbusCore.ModbusDataModel;

namespace ModbusSlave;

public enum DataSimulationMode
{
    Increment,
    Random
}

public sealed record DataSimulationSettings
{
    public byte UnitId { get; init; } = 1;
    public ModbusDataArea Area { get; init; } = ModbusDataArea.HoldingRegisters;
    public ushort StartAddress { get; init; }
    public int RegisterCount { get; init; } = 1;
    public int IntervalMilliseconds { get; init; } = 100;
    public DataSimulationMode Mode { get; init; } = DataSimulationMode.Increment;
    public ModbusDataType DataType { get; init; } = ModbusDataType.UInt16;
    public ModbusByteOrder ByteOrder { get; init; } = ModbusByteOrder.Order1032;
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 100;
    public double Step { get; init; } = 1;
}

public sealed record NoiseSimulationSettings
{
    public int MinimumIntervalMilliseconds { get; init; }
    public int MaximumIntervalMilliseconds { get; init; } = 29;
    public int MinimumLength { get; init; }
    public int MaximumLength { get; init; } = 9;
    public bool SendToSerial { get; init; } = true;
    public bool SendToTcp { get; init; } = true;
}
