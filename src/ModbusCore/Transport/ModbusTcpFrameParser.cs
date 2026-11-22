// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Runtime.InteropServices;
using ModbusCore.ModbusProtocol;

namespace ModbusCore.Transport;

public sealed class ModbusTcpFrameParser
{
    private readonly List<byte> _buffer = [];

    public int BufferedByteCount => _buffer.Count;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            _buffer.AddRange(data.ToArray());
        }
    }

    public bool TryReadFrame(out ModbusTcpFrame? frame)
    {
        frame = null;
        try
        {
            if (!ModbusTcpFrame.TryParse(
                    CollectionsMarshal.AsSpan(_buffer),
                    out frame,
                    out int bytesConsumed))
            {
                return false;
            }

            _buffer.RemoveRange(0, bytesConsumed);
            return true;
        }
        catch
        {
            _buffer.Clear();
            frame = null;
            throw;
        }
    }

    public void Reset()
    {
        _buffer.Clear();
    }
}
