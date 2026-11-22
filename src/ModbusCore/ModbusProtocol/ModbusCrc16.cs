// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusCore.ModbusProtocol;

public static class ModbusCrc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;

        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (ushort)((crc >> 1) ^ 0xA001)
                    : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    public static bool Validate(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3)
        {
            return false;
        }

        ushort expected = (ushort)(frame[^2] | (frame[^1] << 8));
        return Compute(frame[..^2]) == expected;
    }
}
