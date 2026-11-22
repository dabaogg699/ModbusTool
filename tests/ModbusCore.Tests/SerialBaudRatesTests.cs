// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.Transport;

namespace ModbusCore.Tests;

public sealed class SerialBaudRatesTests
{
    [Fact]
    public void PresetsMatchSupportedDropDownValues()
    {
        int[] expected = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

        Assert.Equal(expected, SerialBaudRates.Presets);
    }

    [Theory]
    [InlineData("1200", 1200)]
    [InlineData("921600", 921600)]
    [InlineData("12345", 12345)]
    [InlineData(" 38400 ", 38400)]
    public void AcceptsPresetAndCustomPositiveIntegerValues(string text, int expected)
    {
        bool valid = SerialBaudRates.TryValidate(text, out int baudRate, out string error);

        Assert.True(valid);
        Assert.Equal(expected, baudRate);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-9600")]
    public void RejectsInvalidValuesWithExplicitFeedback(string? text)
    {
        bool valid = SerialBaudRates.TryValidate(text, out int baudRate, out string error);

        Assert.False(valid);
        Assert.Equal(0, baudRate);
        Assert.Equal("波特率必须为大于 0 的整数。", error);
    }
}
