// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusCore.ModbusProtocol;

public enum ModbusFunction : byte
{
    ReadCoils = 0x01,
    ReadDiscreteInputs = 0x02,
    ReadHoldingRegisters = 0x03,
    ReadInputRegisters = 0x04,
    WriteSingleCoil = 0x05,
    WriteSingleRegister = 0x06,
    WriteMultipleCoils = 0x0F,
    WriteMultipleRegisters = 0x10
}
