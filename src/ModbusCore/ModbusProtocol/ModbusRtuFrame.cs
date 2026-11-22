// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusCore.ModbusProtocol;

public sealed class ModbusRtuFrame
{
    public ModbusRtuFrame(byte unitId, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 1 || pdu.Length > 253)
        {
            throw new ArgumentException("An RTU PDU must contain between 1 and 253 bytes.", nameof(pdu));
        }

        UnitId = unitId;
        Pdu = pdu.ToArray();
    }

    public byte UnitId { get; }

    public ReadOnlyMemory<byte> Pdu { get; }

    public static ModbusRtuFrame FromRequest(byte unitId, ModbusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ModbusRtuFrame(unitId, request.ToArray());
    }

    public static ModbusRtuFrame FromResponse(byte unitId, ModbusResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new ModbusRtuFrame(unitId, response.ToArray());
    }

    public byte[] ToArray()
    {
        ReadOnlySpan<byte> pdu = Pdu.Span;
        byte[] frame = new byte[pdu.Length + 3];
        frame[0] = UnitId;
        pdu.CopyTo(frame.AsSpan(1));
        ushort crc = ModbusCrc16.Compute(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    public static ModbusRtuFrame Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4 || frame.Length > 256)
        {
            throw new ModbusProtocolException("The RTU frame length is invalid.");
        }

        if (!ModbusCrc16.Validate(frame))
        {
            throw new ModbusProtocolException("The RTU CRC is invalid.");
        }

        return new ModbusRtuFrame(frame[0], frame[1..^2]);
    }
}
