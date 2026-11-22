// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Runtime.InteropServices;
using ModbusCore.ModbusProtocol;

namespace ModbusCore.Transport;

public enum ModbusRtuFrameKind
{
    Request,
    Response
}

public sealed class ModbusRtuFrameParser
{
    private readonly List<byte> _buffer = [];

    public ModbusRtuFrameParser(ModbusRtuFrameKind frameKind)
    {
        FrameKind = frameKind;
    }

    public ModbusRtuFrameKind FrameKind { get; }

    public int BufferedByteCount => _buffer.Count;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            _buffer.AddRange(data.ToArray());
        }
    }

    public bool TryReadFrame(out ModbusRtuFrame? frame)
    {
        frame = null;
        int expectedLength = GetExpectedFrameLength(CollectionsMarshal.AsSpan(_buffer), FrameKind);
        if (expectedLength == 0)
        {
            return false;
        }

        if (expectedLength > 256)
        {
            _buffer.Clear();
            throw new ModbusProtocolException("The expected RTU frame exceeds 256 bytes.");
        }

        if (_buffer.Count < expectedLength)
        {
            return false;
        }

        byte[] bytes = _buffer.GetRange(0, expectedLength).ToArray();
        _buffer.RemoveRange(0, expectedLength);
        frame = ModbusRtuFrame.Parse(bytes);
        return true;
    }

    public ModbusRtuFrame ReadFrameAfterIdle()
    {
        if (_buffer.Count == 0)
        {
            throw new InvalidOperationException("There is no buffered RTU data.");
        }

        byte[] bytes = _buffer.ToArray();
        _buffer.Clear();
        if (bytes.Length < 4)
        {
            throw new ModbusProtocolException("An incomplete RTU frame was discarded after the inter-frame delay.");
        }

        return ModbusRtuFrame.Parse(bytes);
    }

    public void Reset()
    {
        _buffer.Clear();
    }

    private static int GetExpectedFrameLength(ReadOnlySpan<byte> buffer, ModbusRtuFrameKind frameKind)
    {
        if (buffer.Length < 2)
        {
            return 0;
        }

        byte function = buffer[1];
        if (frameKind == ModbusRtuFrameKind.Response && (function & 0x80) != 0)
        {
            return 5;
        }

        return function switch
        {
            0x01 or 0x02 or 0x03 or 0x04 when frameKind == ModbusRtuFrameKind.Request => 8,
            0x05 or 0x06 or 0x0F or 0x10 when frameKind == ModbusRtuFrameKind.Request
                => GetRequestWriteLength(buffer, function),
            0x01 or 0x02 or 0x03 or 0x04 => GetReadResponseLength(buffer),
            0x05 or 0x06 or 0x0F or 0x10 => 8,
            _ => 0
        };
    }

    private static int GetRequestWriteLength(ReadOnlySpan<byte> buffer, byte function)
    {
        if (function is 0x05 or 0x06)
        {
            return 8;
        }

        return buffer.Length < 7 ? 0 : 9 + buffer[6];
    }

    private static int GetReadResponseLength(ReadOnlySpan<byte> buffer)
    {
        return buffer.Length < 3 ? 0 : 5 + buffer[2];
    }
}
