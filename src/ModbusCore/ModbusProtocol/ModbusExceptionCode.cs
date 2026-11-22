// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusCore.ModbusProtocol;

public enum ModbusExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    SlaveDeviceFailure = 0x04,
    Acknowledge = 0x05,
    SlaveDeviceBusy = 0x06,
    NegativeAcknowledge = 0x07,
    MemoryParityError = 0x08,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetDeviceFailedToRespond = 0x0B
}

public sealed class ModbusProtocolException : FormatException
{
    public ModbusProtocolException(
        string message,
        ModbusExceptionCode exceptionCode = ModbusExceptionCode.IllegalDataValue)
        : base(message)
    {
        ExceptionCode = exceptionCode;
    }

    public ModbusExceptionCode ExceptionCode { get; }
}
