// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Buffers.Binary;

namespace ModbusCore.ModbusProtocol;

public sealed class ModbusTcpFrame
{
    public ModbusTcpFrame(ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 1 || pdu.Length > 253)
        {
            throw new ArgumentException("A TCP PDU must contain between 1 and 253 bytes.", nameof(pdu));
        }

        TransactionId = transactionId;
        UnitId = unitId;
        Pdu = pdu.ToArray();
    }

    public ushort TransactionId { get; }

    public byte UnitId { get; }

    public ReadOnlyMemory<byte> Pdu { get; }

    public static ModbusTcpFrame FromRequest(ushort transactionId, byte unitId, ModbusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ModbusTcpFrame(transactionId, unitId, request.ToArray());
    }

    public static ModbusTcpFrame FromResponse(ushort transactionId, byte unitId, ModbusResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new ModbusTcpFrame(transactionId, unitId, response.ToArray());
    }

    public byte[] ToArray()
    {
        ReadOnlySpan<byte> pdu = Pdu.Span;
        byte[] frame = new byte[pdu.Length + 7];
        BinaryPrimitives.WriteUInt16BigEndian(frame, TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = UnitId;
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }

    public static ModbusTcpFrame Parse(ReadOnlySpan<byte> frame)
    {
        if (!TryParse(frame, out ModbusTcpFrame? parsed, out int consumed) || consumed != frame.Length)
        {
            throw new ModbusProtocolException("The TCP frame length is invalid.");
        }

        return parsed!;
    }

    public static bool TryParse(
        ReadOnlySpan<byte> buffer,
        out ModbusTcpFrame? frame,
        out int bytesConsumed)
    {
        frame = null;
        bytesConsumed = 0;
        if (buffer.Length < 6)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(buffer[2..]) != 0)
        {
            throw new ModbusProtocolException("The MBAP protocol identifier must be zero.");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(buffer[4..]);
        if (length < 2 || length > 254)
        {
            throw new ModbusProtocolException("The MBAP length field is invalid.");
        }

        int frameLength = 6 + length;
        if (buffer.Length < frameLength)
        {
            return false;
        }

        frame = new ModbusTcpFrame(
            BinaryPrimitives.ReadUInt16BigEndian(buffer),
            buffer[6],
            buffer.Slice(7, length - 1));
        bytesConsumed = frameLength;
        return true;
    }
}
