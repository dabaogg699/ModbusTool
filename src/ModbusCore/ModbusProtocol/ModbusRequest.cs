// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Buffers.Binary;

namespace ModbusCore.ModbusProtocol;

/// <summary>不可变 Modbus 请求模型，集中校验功能码、地址范围和读写数量，并生成标准 PDU。</summary>
public sealed class ModbusRequest
{
    private ModbusRequest(
        ModbusFunction function,
        ushort address,
        ushort quantity,
        ushort value = 0,
        ReadOnlyMemory<bool>? coils = null,
        ReadOnlyMemory<ushort>? registers = null)
    {
        Function = function;
        Address = address;
        Quantity = quantity;
        Value = value;
        Coils = coils ?? ReadOnlyMemory<bool>.Empty;
        Registers = registers ?? ReadOnlyMemory<ushort>.Empty;
    }

    public ModbusFunction Function { get; }

    public ushort Address { get; }

    public ushort Quantity { get; }

    public ushort Value { get; }

    public bool SingleCoilValue => Value == 0xFF00;

    public ReadOnlyMemory<bool> Coils { get; }

    public ReadOnlyMemory<ushort> Registers { get; }

    public static ModbusRequest CreateRead(ModbusFunction function, ushort address, ushort quantity)
    {
        int maximum = function switch
        {
            ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs => 2000,
            ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters => 125,
            _ => throw new ModbusProtocolException(
                $"Function 0x{(byte)function:X2} is not a read function.",
                ModbusExceptionCode.IllegalFunction)
        };

        if (quantity < 1 || quantity > maximum)
        {
            throw new ModbusProtocolException($"Read quantity must be between 1 and {maximum}.");
        }

        ValidateRange(address, quantity);
        return new ModbusRequest(function, address, quantity);
    }

    public static ModbusRequest CreateWriteSingleCoil(ushort address, bool value)
    {
        return new ModbusRequest(
            ModbusFunction.WriteSingleCoil,
            address,
            1,
            value ? (ushort)0xFF00 : (ushort)0);
    }

    public static ModbusRequest CreateWriteSingleRegister(ushort address, ushort value)
    {
        return new ModbusRequest(ModbusFunction.WriteSingleRegister, address, 1, value);
    }

    public static ModbusRequest CreateWriteMultipleCoils(ushort address, ReadOnlySpan<bool> values)
    {
        ValidateWriteRange(address, values.Length, 1968);
        return new ModbusRequest(
            ModbusFunction.WriteMultipleCoils,
            address,
            (ushort)values.Length,
            coils: values.ToArray());
    }

    public static ModbusRequest CreateWriteMultipleRegisters(ushort address, ReadOnlySpan<ushort> values)
    {
        ValidateWriteRange(address, values.Length, 123);
        return new ModbusRequest(
            ModbusFunction.WriteMultipleRegisters,
            address,
            (ushort)values.Length,
            registers: values.ToArray());
    }

    public byte[] ToArray()
    {
        if (Function is ModbusFunction.WriteMultipleCoils or ModbusFunction.WriteMultipleRegisters)
        {
            return CreateMultipleWritePdu();
        }

        byte[] pdu = new byte[5];
        pdu[0] = (byte)Function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), Address);
        BinaryPrimitives.WriteUInt16BigEndian(
            pdu.AsSpan(3),
            IsReadFunction(Function) ? Quantity : Value);
        return pdu;
    }

    public static ModbusRequest Parse(ReadOnlySpan<byte> pdu)
    {
        if (pdu.IsEmpty)
        {
            throw new ModbusProtocolException("The request PDU is empty.");
        }

        ModbusFunction function = (ModbusFunction)pdu[0];
        if (!Enum.IsDefined(function))
        {
            throw new ModbusProtocolException(
                $"Unsupported function 0x{pdu[0]:X2}.",
                ModbusExceptionCode.IllegalFunction);
        }

        if (IsReadFunction(function))
        {
            EnsureLength(pdu, 5);
            return CreateRead(
                function,
                BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]),
                BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]));
        }

        if (function is ModbusFunction.WriteSingleCoil or ModbusFunction.WriteSingleRegister)
        {
            EnsureLength(pdu, 5);
            ushort address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);

            if (function == ModbusFunction.WriteSingleCoil)
            {
                if (value is not (0 or 0xFF00))
                {
                    throw new ModbusProtocolException("A single-coil value must be 0x0000 or 0xFF00.");
                }

                return CreateWriteSingleCoil(address, value == 0xFF00);
            }

            return CreateWriteSingleRegister(address, value);
        }

        EnsureMinimumLength(pdu, 6);
        ushort startAddress = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        ushort quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        int byteCount = pdu[5];
        EnsureLength(pdu, 6 + byteCount);

        if (function == ModbusFunction.WriteMultipleCoils)
        {
            if (byteCount != (quantity + 7) / 8)
            {
                throw new ModbusProtocolException("The coil byte count does not match the quantity.");
            }

            bool[] coils = new bool[quantity];
            for (int index = 0; index < coils.Length; index++)
            {
                coils[index] = (pdu[6 + index / 8] & (1 << (index % 8))) != 0;
            }

            return CreateWriteMultipleCoils(startAddress, coils);
        }

        if (byteCount != quantity * 2)
        {
            throw new ModbusProtocolException("The register byte count does not match the quantity.");
        }

        ushort[] registers = new ushort[quantity];
        for (int index = 0; index < registers.Length; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(6 + index * 2)..]);
        }

        return CreateWriteMultipleRegisters(startAddress, registers);
    }

    private byte[] CreateMultipleWritePdu()
    {
        int byteCount = Function == ModbusFunction.WriteMultipleCoils
            ? (Quantity + 7) / 8
            : Quantity * 2;
        byte[] pdu = new byte[6 + byteCount];
        pdu[0] = (byte)Function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), Address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), Quantity);
        pdu[5] = (byte)byteCount;

        if (Function == ModbusFunction.WriteMultipleCoils)
        {
            ReadOnlySpan<bool> values = Coils.Span;
            for (int index = 0; index < values.Length; index++)
            {
                if (values[index])
                {
                    pdu[6 + index / 8] |= (byte)(1 << (index % 8));
                }
            }
        }
        else
        {
            ReadOnlySpan<ushort> values = Registers.Span;
            for (int index = 0; index < values.Length; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + index * 2), values[index]);
            }
        }

        return pdu;
    }

    private static bool IsReadFunction(ModbusFunction function)
    {
        return function is ModbusFunction.ReadCoils
            or ModbusFunction.ReadDiscreteInputs
            or ModbusFunction.ReadHoldingRegisters
            or ModbusFunction.ReadInputRegisters;
    }

    private static void ValidateWriteRange(ushort address, int count, int maximum)
    {
        if (count < 1 || count > maximum)
        {
            throw new ModbusProtocolException($"Write quantity must be between 1 and {maximum}.");
        }

        ValidateRange(address, count);
    }

    private static void ValidateRange(ushort address, int count)
    {
        if (address + count > 65536)
        {
            throw new ModbusProtocolException(
                "The requested address range exceeds 0xFFFF.",
                ModbusExceptionCode.IllegalDataAddress);
        }
    }

    private static void EnsureLength(ReadOnlySpan<byte> pdu, int expected)
    {
        if (pdu.Length != expected)
        {
            throw new ModbusProtocolException($"Expected a {expected}-byte PDU, received {pdu.Length} bytes.");
        }
    }

    private static void EnsureMinimumLength(ReadOnlySpan<byte> pdu, int minimum)
    {
        if (pdu.Length < minimum)
        {
            throw new ModbusProtocolException($"Expected at least {minimum} PDU bytes, received {pdu.Length}.");
        }
    }
}
