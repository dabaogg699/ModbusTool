// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Globalization;
using System.IO.Ports;

namespace ModbusCore.Transport;

public sealed record SerialTransportSettings
{
    public required string PortName { get; init; }

    public int BaudRate { get; init; } = 9600;

    public Parity Parity { get; init; } = Parity.None;

    public int DataBits { get; init; } = 8;

    public StopBits StopBits { get; init; } = StopBits.One;

    public Handshake Handshake { get; init; } = Handshake.None;

    public TimeSpan? InterFrameDelay { get; init; }

    internal TimeSpan GetInterFrameDelay()
    {
        return InterFrameDelay ?? ModbusRtuTiming.CalculateInterFrameDelay(
            BaudRate,
            DataBits,
            Parity,
            StopBits);
    }
}

/// <summary>主从站共用的串口波特率预置值与自定义输入校验。</summary>
public static class SerialBaudRates
{
    public static IReadOnlyList<int> Presets { get; } =
        [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    public static bool TryValidate(string? text, out int baudRate, out string error)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out baudRate)
            || baudRate <= 0)
        {
            baudRate = 0;
            error = "波特率必须为大于 0 的整数。";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

public static class ModbusRtuTiming
{
    public static TimeSpan CalculateInterFrameDelay(
        int baudRate,
        int dataBits,
        Parity parity,
        StopBits stopBits)
    {
        ArgumentGuard.ThrowIfLessThanOrEqual(baudRate, 0);
        if (dataBits < 5 || dataBits > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(dataBits));
        }

        if (baudRate > 19200)
        {
            return TimeSpan.FromMilliseconds(1.75);
        }

        double stopBitCount = stopBits switch
        {
            StopBits.One => 1,
            StopBits.OnePointFive => 1.5,
            StopBits.Two => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(stopBits))
        };
        double bitsPerCharacter = 1 + dataBits + (parity == Parity.None ? 0 : 1) + stopBitCount;
        return TimeSpan.FromMilliseconds(3.5 * bitsPerCharacter * 1000 / baudRate);
    }
}
