// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusProtocol;

namespace ModbusSlave;

[Flags]
public enum SlaveFaultKind
{
    None = 0,
    Dropped = 1,
    Delayed = 2,
    InvalidValue = 4,
    ForcedException = 8,
    InvalidCrc = 16
}

public sealed record FaultInjectionSettings
{
    public bool Enabled { get; init; }
    public double DropProbability { get; init; }
    public double InvalidValueProbability { get; init; }
    public double InvalidCrcProbability { get; init; }
    public int DelayMilliseconds { get; init; }
    public ModbusExceptionCode? ForcedExceptionCode { get; init; }
}
