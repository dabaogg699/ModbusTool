// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusCore.ByteConvert;

public enum ModbusDataType
{
    UInt16,
    Int16,
    UInt32,
    Int32,
    UInt64,
    Int64,
    Float,
    Double
}

public enum ModbusByteOrder
{
    Order1032 = 0,
    Order3210 = 1,
    Order0123 = 2,
    Order2301 = 3
}
