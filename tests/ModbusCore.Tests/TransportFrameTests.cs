// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusProtocol;

namespace ModbusCore.Tests;

public sealed class TransportFrameTests
{
    [Fact]
    public void RtuFrameMatchesStandardRequestAndValidatesCrc()
    {
        byte[] expected = Convert.FromHexString("01030000000AC5CD");
        ModbusRtuFrame frame = ModbusRtuFrame.Parse(expected);

        Assert.Equal(1, frame.UnitId);
        Assert.Equal("030000000A", Convert.ToHexString(frame.Pdu.ToArray()));
        Assert.Equal(expected, frame.ToArray());

        byte[] corrupted = [.. expected];
        corrupted[3] ^= 0x01;
        Assert.Throws<ModbusProtocolException>(() => ModbusRtuFrame.Parse(corrupted));
    }

    [Fact]
    public void RtuRequestFactoryAddsStationAndCrc()
    {
        ModbusRequest request = ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 10);
        byte[] frame = ModbusRtuFrame.FromRequest(1, request).ToArray();

        Assert.Equal("01030000000AC5CD", Convert.ToHexString(frame));
    }

    [Fact]
    public void TcpFrameRoundTripsTransactionUnitAndPdu()
    {
        ModbusRequest request = ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 10);
        ModbusTcpFrame frame = ModbusTcpFrame.FromRequest(0x1234, 1, request);
        byte[] bytes = frame.ToArray();

        Assert.Equal("12340000000601030000000A", Convert.ToHexString(bytes));
        ModbusTcpFrame parsed = ModbusTcpFrame.Parse(bytes);
        Assert.Equal(0x1234, parsed.TransactionId);
        Assert.Equal(1, parsed.UnitId);
        Assert.Equal(request.ToArray(), parsed.Pdu.ToArray());
    }

    [Fact]
    public void TcpTryParseWaitsForCompleteFrameAndReportsConsumedBytes()
    {
        ModbusTcpFrame first = ModbusTcpFrame.FromRequest(
            1,
            1,
            ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1));
        ModbusTcpFrame second = ModbusTcpFrame.FromRequest(
            2,
            2,
            ModbusRequest.CreateRead(ModbusFunction.ReadInputRegisters, 10, 2));
        byte[] firstBytes = first.ToArray();
        byte[] secondBytes = second.ToArray();

        Assert.False(ModbusTcpFrame.TryParse(firstBytes.AsSpan(0, 5), out _, out _));
        Assert.False(ModbusTcpFrame.TryParse(firstBytes.AsSpan(0, firstBytes.Length - 1), out _, out _));

        byte[] combined = [.. firstBytes, .. secondBytes];
        Assert.True(ModbusTcpFrame.TryParse(combined, out ModbusTcpFrame? parsed, out int consumed));
        Assert.Equal(firstBytes.Length, consumed);
        Assert.Equal(1, parsed!.TransactionId);
        Assert.True(ModbusTcpFrame.TryParse(combined[consumed..], out parsed, out consumed));
        Assert.Equal(secondBytes.Length, consumed);
        Assert.Equal(2, parsed!.TransactionId);
    }

    [Fact]
    public void TcpParserRejectsNonModbusProtocolIdentifier()
    {
        byte[] bytes = Convert.FromHexString("000100010006010300000001");

        Assert.Throws<ModbusProtocolException>(() => ModbusTcpFrame.Parse(bytes));
    }
}
