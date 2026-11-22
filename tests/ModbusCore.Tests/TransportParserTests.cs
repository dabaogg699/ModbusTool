// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO.Ports;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;

namespace ModbusCore.Tests;

public sealed class TransportParserTests
{
    [Fact]
    public void RtuParserUsesExpectedLengthAndSeparatesStickyRequests()
    {
        ModbusRtuFrame first = ModbusRtuFrame.FromRequest(
            1,
            ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 10));
        ModbusRtuFrame second = ModbusRtuFrame.FromRequest(
            2,
            ModbusRequest.CreateRead(ModbusFunction.ReadInputRegisters, 10, 2));
        byte[] firstBytes = first.ToArray();
        ModbusRtuFrameParser parser = new(ModbusRtuFrameKind.Request);

        parser.Append(firstBytes.AsSpan(0, 3));
        Assert.False(parser.TryReadFrame(out _));

        parser.Append([.. firstBytes.AsSpan(3), .. second.ToArray()]);
        Assert.True(parser.TryReadFrame(out ModbusRtuFrame? parsedFirst));
        Assert.Equal(firstBytes, parsedFirst!.ToArray());
        Assert.True(parser.TryReadFrame(out ModbusRtuFrame? parsedSecond));
        Assert.Equal(second.ToArray(), parsedSecond!.ToArray());
        Assert.False(parser.TryReadFrame(out _));
        Assert.Equal(0, parser.BufferedByteCount);
    }

    [Fact]
    public void RtuMultipleWriteParserWaitsForByteCountAndPayload()
    {
        ModbusRequest request = ModbusRequest.CreateWriteMultipleCoils(
            20,
            [true, false, true, true, false, false, true, false, true]);
        byte[] bytes = ModbusRtuFrame.FromRequest(1, request).ToArray();
        ModbusRtuFrameParser parser = new(ModbusRtuFrameKind.Request);

        parser.Append(bytes.AsSpan(0, 6));
        Assert.False(parser.TryReadFrame(out _));
        parser.Append(bytes.AsSpan(6, 1));
        Assert.False(parser.TryReadFrame(out _));
        parser.Append(bytes.AsSpan(7));

        Assert.True(parser.TryReadFrame(out ModbusRtuFrame? parsed));
        Assert.Equal(bytes, parsed!.ToArray());
    }

    [Fact]
    public void RtuResponseParserUsesVariableByteCount()
    {
        ModbusRequest request = ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 2);
        ModbusResponse response = ModbusResponse.CreateReadRegistersResponse(request, [0x1234, 0xABCD]);
        byte[] bytes = ModbusRtuFrame.FromResponse(1, response).ToArray();
        ModbusRtuFrameParser parser = new(ModbusRtuFrameKind.Response);

        parser.Append(bytes.AsSpan(0, 2));
        Assert.False(parser.TryReadFrame(out _));
        parser.Append(bytes.AsSpan(2, 1));
        Assert.False(parser.TryReadFrame(out _));
        parser.Append(bytes.AsSpan(3));

        Assert.True(parser.TryReadFrame(out ModbusRtuFrame? parsed));
        Assert.Equal(bytes, parsed!.ToArray());
    }

    [Fact]
    public void RtuCrcErrorDoesNotConsumeFollowingValidFrame()
    {
        byte[] invalid = ModbusRtuFrame.FromRequest(
            1,
            ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1)).ToArray();
        ModbusRtuFrame valid = ModbusRtuFrame.FromRequest(
            2,
            ModbusRequest.CreateRead(ModbusFunction.ReadInputRegisters, 0, 1));
        invalid[^1] ^= 0xFF;
        ModbusRtuFrameParser parser = new(ModbusRtuFrameKind.Request);
        parser.Append([.. invalid, .. valid.ToArray()]);

        Assert.Throws<ModbusProtocolException>(() => parser.TryReadFrame(out _));
        Assert.True(parser.TryReadFrame(out ModbusRtuFrame? parsed));
        Assert.Equal(valid.ToArray(), parsed!.ToArray());
    }

    [Fact]
    public void RtuUnknownFunctionUsesIdleBoundary()
    {
        ModbusRtuFrame expected = new(1, [0x40, 0x01, 0x02]);
        ModbusRtuFrameParser parser = new(ModbusRtuFrameKind.Request);
        parser.Append(expected.ToArray());

        Assert.False(parser.TryReadFrame(out _));
        ModbusRtuFrame actual = parser.ReadFrameAfterIdle();
        Assert.Equal(expected.ToArray(), actual.ToArray());
        Assert.Equal(0, parser.BufferedByteCount);
    }

    [Fact]
    public void RtuIdleRejectsIncompleteFrameAndClearsBuffer()
    {
        ModbusRtuFrameParser parser = new(ModbusRtuFrameKind.Request);
        parser.Append([0x01, 0x03, 0x00]);

        Assert.Throws<ModbusProtocolException>(() => parser.ReadFrameAfterIdle());
        Assert.Equal(0, parser.BufferedByteCount);
    }

    [Fact]
    public void RtuTimingUsesCharacterTimeAndHighBaudFixedDelay()
    {
        TimeSpan lowSpeed = ModbusRtuTiming.CalculateInterFrameDelay(
            9600,
            8,
            Parity.None,
            StopBits.One);
        TimeSpan highSpeed = ModbusRtuTiming.CalculateInterFrameDelay(
            115200,
            8,
            Parity.Even,
            StopBits.One);

        Assert.Equal(TimeSpan.FromMilliseconds(3.5 * 10 * 1000 / 9600), lowSpeed);
        Assert.Equal(TimeSpan.FromMilliseconds(1.75), highSpeed);
    }

    [Fact]
    public void TcpIncrementalParserHandlesFragmentationAndCoalescing()
    {
        ModbusTcpFrame first = CreateRequestFrame(1, 1);
        ModbusTcpFrame second = CreateRequestFrame(2, 2);
        byte[] firstBytes = first.ToArray();
        ModbusTcpFrameParser parser = new();

        parser.Append(firstBytes.AsSpan(0, 4));
        Assert.False(parser.TryReadFrame(out _));
        parser.Append([.. firstBytes.AsSpan(4), .. second.ToArray()]);

        Assert.True(parser.TryReadFrame(out ModbusTcpFrame? parsedFirst));
        Assert.Equal(firstBytes, parsedFirst!.ToArray());
        Assert.True(parser.TryReadFrame(out ModbusTcpFrame? parsedSecond));
        Assert.Equal(second.ToArray(), parsedSecond!.ToArray());
        Assert.False(parser.TryReadFrame(out _));
    }

    [Fact]
    public void TcpParserRecoversAfterInvalidMbapHeader()
    {
        ModbusTcpFrameParser parser = new();
        parser.Append(Convert.FromHexString("000100010006010300000001"));
        Assert.Throws<ModbusProtocolException>(() => parser.TryReadFrame(out _));
        Assert.Equal(0, parser.BufferedByteCount);

        ModbusTcpFrame expected = CreateRequestFrame(7, 1);
        parser.Append(expected.ToArray());
        Assert.True(parser.TryReadFrame(out ModbusTcpFrame? actual));
        Assert.Equal(expected.ToArray(), actual!.ToArray());
    }

    internal static ModbusTcpFrame CreateRequestFrame(ushort transactionId, byte unitId)
    {
        return ModbusTcpFrame.FromRequest(
            transactionId,
            unitId,
            ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1));
    }
}
