// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusProtocol;

namespace ModbusMaster;

public interface IMasterFrameCodec
{
    byte[] Encode(byte unitId, ushort transactionId, ModbusRequest request);

    ModbusResponse? Decode(
        ReadOnlySpan<byte> frame,
        byte unitId,
        ushort transactionId,
        ModbusRequest request);
}

public sealed class RtuMasterFrameCodec : IMasterFrameCodec
{
    public byte[] Encode(byte unitId, ushort transactionId, ModbusRequest request)
    {
        return ModbusRtuFrame.FromRequest(unitId, request).ToArray();
    }

    public ModbusResponse? Decode(
        ReadOnlySpan<byte> frame,
        byte unitId,
        ushort transactionId,
        ModbusRequest request)
    {
        ModbusRtuFrame rtuFrame = ModbusRtuFrame.Parse(frame);
        return rtuFrame.UnitId != unitId
            ? null
            : ModbusResponse.Parse(rtuFrame.Pdu.Span, request);
    }
}

public sealed class TcpMasterFrameCodec : IMasterFrameCodec
{
    public byte[] Encode(byte unitId, ushort transactionId, ModbusRequest request)
    {
        return ModbusTcpFrame.FromRequest(transactionId, unitId, request).ToArray();
    }

    public ModbusResponse? Decode(
        ReadOnlySpan<byte> frame,
        byte unitId,
        ushort transactionId,
        ModbusRequest request)
    {
        ModbusTcpFrame tcpFrame = ModbusTcpFrame.Parse(frame);
        return tcpFrame.TransactionId != transactionId || tcpFrame.UnitId != unitId
            ? null
            : ModbusResponse.Parse(tcpFrame.Pdu.Span, request);
    }
}
