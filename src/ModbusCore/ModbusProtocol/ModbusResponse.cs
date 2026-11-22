// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Buffers.Binary;

namespace ModbusCore.ModbusProtocol;

public sealed class ModbusResponse
{
    private ModbusResponse(
        ModbusFunction function,
        ushort address = 0,
        ushort quantity = 0,
        ushort value = 0,
        ReadOnlyMemory<byte>? data = null,
        ModbusExceptionCode? exceptionCode = null)
    {
        Function = function;
        Address = address;
        Quantity = quantity;
        Value = value;
        Data = data ?? ReadOnlyMemory<byte>.Empty;
        ExceptionCode = exceptionCode;
    }

    public ModbusFunction Function { get; }

    public ushort Address { get; }

    public ushort Quantity { get; }

    public ushort Value { get; }

    public ReadOnlyMemory<byte> Data { get; }

    public ModbusExceptionCode? ExceptionCode { get; }

    public bool IsException => ExceptionCode.HasValue;

    public static ModbusResponse CreateReadBitsResponse(
        ModbusRequest request,
        ReadOnlySpan<bool> values)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Function is not (ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs))
        {
            throw new ArgumentException("The request is not a bit-read request.", nameof(request));
        }

        EnsureValueCount(request, values.Length);
        byte[] data = new byte[(values.Length + 7) / 8];
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index])
            {
                data[index / 8] |= (byte)(1 << (index % 8));
            }
        }

        return new ModbusResponse(
            request.Function,
            request.Address,
            request.Quantity,
            data: data);
    }

    public static ModbusResponse CreateReadRegistersResponse(
        ModbusRequest request,
        ReadOnlySpan<ushort> values)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Function is not (ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters))
        {
            throw new ArgumentException("The request is not a register-read request.", nameof(request));
        }

        EnsureValueCount(request, values.Length);
        byte[] data = new byte[values.Length * 2];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(index * 2), values[index]);
        }

        return new ModbusResponse(
            request.Function,
            request.Address,
            request.Quantity,
            data: data);
    }

    public static ModbusResponse CreateWriteResponse(ModbusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Function is not (ModbusFunction.WriteSingleCoil
            or ModbusFunction.WriteSingleRegister
            or ModbusFunction.WriteMultipleCoils
            or ModbusFunction.WriteMultipleRegisters))
        {
            throw new ArgumentException("The request is not a write request.", nameof(request));
        }

        return new ModbusResponse(
            request.Function,
            request.Address,
            request.Quantity,
            request.Value);
    }

    public static ModbusResponse CreateExceptionResponse(
        ModbusRequest request,
        ModbusExceptionCode exceptionCode)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateExceptionResponse(request.Function, exceptionCode);
    }

    public static ModbusResponse CreateExceptionResponse(
        ModbusFunction function,
        ModbusExceptionCode exceptionCode)
    {
        if (!Enum.IsDefined(exceptionCode))
        {
            throw new ArgumentOutOfRangeException(nameof(exceptionCode));
        }

        return new ModbusResponse(function, exceptionCode: exceptionCode);
    }

    public byte[] ToArray()
    {
        if (IsException)
        {
            return [(byte)((byte)Function | 0x80), (byte)ExceptionCode!.Value];
        }

        if (IsReadFunction(Function))
        {
            byte[] pdu = new byte[Data.Length + 2];
            pdu[0] = (byte)Function;
            pdu[1] = (byte)Data.Length;
            Data.Span.CopyTo(pdu.AsSpan(2));
            return pdu;
        }

        byte[] response = new byte[5];
        response[0] = (byte)Function;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(1), Address);
        BinaryPrimitives.WriteUInt16BigEndian(
            response.AsSpan(3),
            Function is ModbusFunction.WriteSingleCoil or ModbusFunction.WriteSingleRegister
                ? Value
                : Quantity);
        return response;
    }

    public static ModbusResponse Parse(ReadOnlySpan<byte> pdu, ModbusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (pdu.IsEmpty)
        {
            throw new ModbusProtocolException("The response PDU is empty.");
        }

        byte expectedExceptionFunction = (byte)((byte)request.Function | 0x80);
        if (pdu[0] == expectedExceptionFunction)
        {
            if (pdu.Length != 2)
            {
                throw new ModbusProtocolException("An exception response must contain exactly two bytes.");
            }

            ModbusExceptionCode code = (ModbusExceptionCode)pdu[1];
            if (!Enum.IsDefined(code))
            {
                throw new ModbusProtocolException($"Unknown Modbus exception code 0x{pdu[1]:X2}.");
            }

            return new ModbusResponse(request.Function, exceptionCode: code);
        }

        if (pdu[0] != (byte)request.Function)
        {
            throw new ModbusProtocolException("The response function does not match the request.");
        }

        if (IsReadFunction(request.Function))
        {
            int expectedDataLength = request.Function is ModbusFunction.ReadCoils
                or ModbusFunction.ReadDiscreteInputs
                ? (request.Quantity + 7) / 8
                : request.Quantity * 2;

            if (pdu.Length != expectedDataLength + 2 || pdu[1] != expectedDataLength)
            {
                throw new ModbusProtocolException("The response byte count does not match the request.");
            }

            return new ModbusResponse(
                request.Function,
                request.Address,
                request.Quantity,
                data: pdu[2..].ToArray());
        }

        if (pdu.Length != 5)
        {
            throw new ModbusProtocolException("A write response must contain exactly five bytes.");
        }

        ushort address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        ushort expectedValue = request.Function is ModbusFunction.WriteSingleCoil
            or ModbusFunction.WriteSingleRegister
            ? request.Value
            : request.Quantity;

        if (address != request.Address || value != expectedValue)
        {
            throw new ModbusProtocolException("The write response echo does not match the request.");
        }

        return new ModbusResponse(request.Function, address, request.Quantity, value);
    }

    public bool[] GetBits()
    {
        if (IsException || Function is not (ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs))
        {
            throw new InvalidOperationException("The response does not contain bit data.");
        }

        ReadOnlySpan<byte> data = Data.Span;
        bool[] values = new bool[Quantity];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = (data[index / 8] & (1 << (index % 8))) != 0;
        }

        return values;
    }

    public ushort[] GetRegisters()
    {
        if (IsException || Function is not (ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters))
        {
            throw new InvalidOperationException("The response does not contain register data.");
        }

        ReadOnlySpan<byte> data = Data.Span;
        ushort[] values = new ushort[Quantity];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt16BigEndian(data[(index * 2)..]);
        }

        return values;
    }

    private static bool IsReadFunction(ModbusFunction function)
    {
        return function is ModbusFunction.ReadCoils
            or ModbusFunction.ReadDiscreteInputs
            or ModbusFunction.ReadHoldingRegisters
            or ModbusFunction.ReadInputRegisters;
    }

    private static void EnsureValueCount(ModbusRequest request, int count)
    {
        if (count != request.Quantity)
        {
            throw new ArgumentException("The value count must match the request quantity.");
        }
    }
}
