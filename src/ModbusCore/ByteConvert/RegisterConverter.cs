// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ModbusCore.ByteConvert;

public static class RegisterConverter
{
    private static readonly byte[][] Order2 =
    [
        [1, 0],
        [1, 0],
        [0, 1],
        [0, 1]
    ];

    private static readonly byte[][] Order4 =
    [
        [1, 0, 3, 2],
        [3, 2, 1, 0],
        [0, 1, 2, 3],
        [2, 3, 0, 1]
    ];

    private static readonly byte[][] Order8 =
    [
        [1, 0, 3, 2, 5, 4, 7, 6],
        [7, 6, 5, 4, 3, 2, 1, 0],
        [0, 1, 2, 3, 4, 5, 6, 7],
        [6, 7, 4, 5, 2, 3, 0, 1]
    ];

    public static int GetRegisterCount(ModbusDataType dataType)
    {
        return dataType switch
        {
            ModbusDataType.UInt16 or ModbusDataType.Int16 => 1,
            ModbusDataType.UInt32 or ModbusDataType.Int32 or ModbusDataType.Float => 2,
            ModbusDataType.UInt64 or ModbusDataType.Int64 or ModbusDataType.Double => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(dataType))
        };
    }

    public static object Decode(
        ReadOnlySpan<ushort> registers,
        ModbusDataType dataType,
        ModbusByteOrder byteOrder)
    {
        int count = GetRegisterCount(dataType);
        if (registers.Length < count)
        {
            throw new ArgumentException($"The value requires {count} registers.", nameof(registers));
        }

        byte[] source = RegistersToBytes(registers[..count]);
        byte[] map = GetMap(count * 2, byteOrder);
        byte[] ordered = new byte[source.Length];
        for (int index = 0; index < ordered.Length; index++)
        {
            ordered[index] = source[map[index]];
        }

        return dataType switch
        {
            ModbusDataType.UInt16 => (object)BinaryPrimitives.ReadUInt16LittleEndian(ordered),
            ModbusDataType.Int16 => (object)BinaryPrimitives.ReadInt16LittleEndian(ordered),
            ModbusDataType.UInt32 => (object)BinaryPrimitives.ReadUInt32LittleEndian(ordered),
            ModbusDataType.Int32 => (object)BinaryPrimitives.ReadInt32LittleEndian(ordered),
            ModbusDataType.UInt64 => (object)BinaryPrimitives.ReadUInt64LittleEndian(ordered),
            ModbusDataType.Int64 => (object)BinaryPrimitives.ReadInt64LittleEndian(ordered),
            ModbusDataType.Float => (object)BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(ordered)),
            ModbusDataType.Double => (object)BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64LittleEndian(ordered)),
            _ => throw new ArgumentOutOfRangeException(nameof(dataType))
        };
    }

    public static ushort[] Encode(
        object value,
        ModbusDataType dataType,
        ModbusByteOrder byteOrder)
    {
        ArgumentNullException.ThrowIfNull(value);
        int count = GetRegisterCount(dataType);
        byte[] ordered = new byte[count * 2];

        switch (dataType)
        {
            case ModbusDataType.UInt16:
                BinaryPrimitives.WriteUInt16LittleEndian(
                    ordered,
                    Convert.ToUInt16(value, CultureInfo.InvariantCulture));
                break;
            case ModbusDataType.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(
                    ordered,
                    Convert.ToInt16(value, CultureInfo.InvariantCulture));
                break;
            case ModbusDataType.UInt32:
                BinaryPrimitives.WriteUInt32LittleEndian(
                    ordered,
                    Convert.ToUInt32(value, CultureInfo.InvariantCulture));
                break;
            case ModbusDataType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(
                    ordered,
                    Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case ModbusDataType.UInt64:
                BinaryPrimitives.WriteUInt64LittleEndian(
                    ordered,
                    Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                break;
            case ModbusDataType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(
                    ordered,
                    Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ModbusDataType.Float:
                BinaryPrimitives.WriteInt32LittleEndian(
                    ordered,
                    BitConverter.SingleToInt32Bits(Convert.ToSingle(value, CultureInfo.InvariantCulture)));
                break;
            case ModbusDataType.Double:
                BinaryPrimitives.WriteInt64LittleEndian(
                    ordered,
                    BitConverter.DoubleToInt64Bits(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dataType));
        }

        byte[] map = GetMap(ordered.Length, byteOrder);
        byte[] source = new byte[ordered.Length];
        for (int index = 0; index < ordered.Length; index++)
        {
            source[map[index]] = ordered[index];
        }

        return BytesToRegisters(source);
    }

    public static string DecodeString(
        ReadOnlySpan<ushort> registers,
        Encoding encoding,
        bool swapAdjacentBytes = false)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        byte[] bytes = RegistersToBytes(registers);
        SwapPairs(bytes, swapAdjacentBytes);
        return encoding.GetString(bytes);
    }

    public static ushort[] EncodeString(
        string value,
        Encoding encoding,
        int registerCount,
        bool swapAdjacentBytes = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(encoding);
        if (registerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(registerCount));
        }

        byte[] encoded = encoding.GetBytes(value);
        if (encoded.Length > registerCount * 2)
        {
            throw new ArgumentException("The encoded string exceeds the selected register capacity.", nameof(value));
        }

        byte[] bytes = new byte[registerCount * 2];
        encoded.CopyTo(bytes, 0);
        SwapPairs(bytes, swapAdjacentBytes);
        return BytesToRegisters(bytes);
    }

    public static string ToHexString(ReadOnlySpan<ushort> registers)
    {
        return Convert.ToHexString(RegistersToBytes(registers));
    }

    private static byte[] GetMap(int byteCount, ModbusByteOrder byteOrder)
    {
        if (!Enum.IsDefined(byteOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        }

        return byteCount switch
        {
            2 => Order2[(int)byteOrder],
            4 => Order4[(int)byteOrder],
            8 => Order8[(int)byteOrder],
            _ => throw new ArgumentOutOfRangeException(nameof(byteCount))
        };
    }

    private static byte[] RegistersToBytes(ReadOnlySpan<ushort> registers)
    {
        byte[] bytes = new byte[registers.Length * 2];
        for (int index = 0; index < registers.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(index * 2), registers[index]);
        }

        return bytes;
    }

    private static ushort[] BytesToRegisters(ReadOnlySpan<byte> bytes)
    {
        ushort[] registers = new ushort[bytes.Length / 2];
        for (int index = 0; index < registers.Length; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(bytes[(index * 2)..]);
        }

        return registers;
    }

    private static void SwapPairs(byte[] bytes, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        for (int index = 0; index < bytes.Length; index += 2)
        {
            (bytes[index], bytes[index + 1]) = (bytes[index + 1], bytes[index]);
        }
    }
}
