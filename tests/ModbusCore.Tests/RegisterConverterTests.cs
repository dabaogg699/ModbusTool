// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Text;
using ModbusCore.ByteConvert;

namespace ModbusCore.Tests;

public sealed class RegisterConverterTests
{
    [Theory]
    [InlineData(ModbusByteOrder.Order1032, 0x33441122u)]
    [InlineData(ModbusByteOrder.Order3210, 0x11223344u)]
    [InlineData(ModbusByteOrder.Order0123, 0x44332211u)]
    [InlineData(ModbusByteOrder.Order2301, 0x22114433u)]
    public void UInt32MatchesLegacyByteOrderTables(ModbusByteOrder byteOrder, uint expected)
    {
        ushort[] registers = [0x1122, 0x3344];

        object decoded = RegisterConverter.Decode(registers, ModbusDataType.UInt32, byteOrder);

        Assert.Equal(expected, Assert.IsType<uint>(decoded));
        Assert.Equal(registers, RegisterConverter.Encode(expected, ModbusDataType.UInt32, byteOrder));
    }

    [Theory]
    [InlineData(ModbusByteOrder.Order1032, (ushort)0x1122)]
    [InlineData(ModbusByteOrder.Order3210, (ushort)0x1122)]
    [InlineData(ModbusByteOrder.Order0123, (ushort)0x2211)]
    [InlineData(ModbusByteOrder.Order2301, (ushort)0x2211)]
    public void UInt16MatchesLegacyByteOrderTables(ModbusByteOrder byteOrder, ushort expected)
    {
        object decoded = RegisterConverter.Decode([0x1122], ModbusDataType.UInt16, byteOrder);

        Assert.Equal(expected, Assert.IsType<ushort>(decoded));
    }

    [Fact]
    public void EveryNumericTypeRoundTripsThroughEveryLegacyByteOrder()
    {
        (ModbusDataType Type, object Value)[] samples =
        [
            (ModbusDataType.UInt16, (ushort)0xABCD),
            (ModbusDataType.Int16, (short)-12345),
            (ModbusDataType.UInt32, 0xDEADBEEFu),
            (ModbusDataType.Int32, -123456789),
            (ModbusDataType.UInt64, 0x0123456789ABCDEFul),
            (ModbusDataType.Int64, -0x0123456789ABCDEL),
            (ModbusDataType.Float, 1234.5f),
            (ModbusDataType.Double, -Math.PI)
        ];

        foreach ((ModbusDataType type, object value) in samples)
        {
            foreach (ModbusByteOrder order in Enum.GetValues<ModbusByteOrder>())
            {
                ushort[] encoded = RegisterConverter.Encode(value, type, order);
                object decoded = RegisterConverter.Decode(encoded, type, order);

                Assert.Equal(RegisterConverter.GetRegisterCount(type), encoded.Length);
                Assert.True(value.Equals(decoded), $"{type}/{order}: expected {value}, actual {decoded}");
            }
        }
    }

    [Fact]
    public void StringConversionSupportsEncodingPaddingAndAdjacentByteSwap()
    {
        ushort[] registers = [0x4142, 0x4344];

        Assert.Equal("ABCD", RegisterConverter.DecodeString(registers, Encoding.ASCII));
        Assert.Equal("BADC", RegisterConverter.DecodeString(registers, Encoding.ASCII, true));
        Assert.Equal(
            [0x4142, 0x4344, 0x0000],
            RegisterConverter.EncodeString("ABCD", Encoding.ASCII, 3));
        Assert.Equal(
            [0x4241, 0x4443, 0x0000],
            RegisterConverter.EncodeString("ABCD", Encoding.ASCII, 3, true));
    }

    [Fact]
    public void HexConversionUsesOriginalRegisterByteSequence()
    {
        Assert.Equal("11223344", RegisterConverter.ToHexString([0x1122, 0x3344]));
    }

    [Fact]
    public void ConversionRejectsInsufficientRegistersAndOversizedStrings()
    {
        Assert.Throws<ArgumentException>(() =>
            RegisterConverter.Decode([1], ModbusDataType.Double, ModbusByteOrder.Order1032));
        Assert.Throws<ArgumentException>(() =>
            RegisterConverter.EncodeString("ABC", Encoding.ASCII, 1));
    }
}
