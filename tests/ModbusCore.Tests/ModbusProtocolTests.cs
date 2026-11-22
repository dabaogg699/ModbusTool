// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusProtocol;

namespace ModbusCore.Tests;

public sealed class ModbusProtocolTests
{
    [Fact]
    public void Crc16MatchesStandardModbusVector()
    {
        byte[] request = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A];

        Assert.Equal(0xCDC5, ModbusCrc16.Compute(request));
        Assert.True(ModbusCrc16.Validate([.. request, 0xC5, 0xCD]));
        Assert.False(ModbusCrc16.Validate([.. request, 0xC5, 0xCC]));
    }

    [Fact]
    public void ReadRequestsRoundTripForEveryReadFunction()
    {
        ModbusFunction[] functions =
        [
            ModbusFunction.ReadCoils,
            ModbusFunction.ReadDiscreteInputs,
            ModbusFunction.ReadHoldingRegisters,
            ModbusFunction.ReadInputRegisters
        ];

        foreach (ModbusFunction function in functions)
        {
            ModbusRequest request = ModbusRequest.CreateRead(function, 20, 10);
            ModbusRequest parsed = ModbusRequest.Parse(request.ToArray());

            Assert.Equal(function, parsed.Function);
            Assert.Equal(20, parsed.Address);
            Assert.Equal(10, parsed.Quantity);
            Assert.Equal(request.ToArray(), parsed.ToArray());
        }
    }

    [Fact]
    public void WriteMultipleCoilsUsesLeastSignificantBitFirstPacking()
    {
        bool[] values = [true, false, true, true, false, false, true, true, true, false];
        ModbusRequest request = ModbusRequest.CreateWriteMultipleCoils(19, values);

        Assert.Equal("0F0013000A02CD01", Convert.ToHexString(request.ToArray()));
        Assert.Equal(values, ModbusRequest.Parse(request.ToArray()).Coils.ToArray());
    }

    [Fact]
    public void WriteMultipleRegistersUsesBigEndianRegisterEncoding()
    {
        ModbusRequest request = ModbusRequest.CreateWriteMultipleRegisters(1, [0x000A, 0x0102]);

        Assert.Equal("100001000204000A0102", Convert.ToHexString(request.ToArray()));
        Assert.Equal([0x000A, 0x0102], ModbusRequest.Parse(request.ToArray()).Registers.ToArray());
    }

    [Fact]
    public void SingleWriteRequestsValidateAndRoundTrip()
    {
        ModbusRequest coil = ModbusRequest.CreateWriteSingleCoil(12, true);
        ModbusRequest register = ModbusRequest.CreateWriteSingleRegister(13, 0xABCD);

        Assert.Equal("05000CFF00", Convert.ToHexString(coil.ToArray()));
        Assert.True(ModbusRequest.Parse(coil.ToArray()).SingleCoilValue);
        Assert.Equal(0xABCD, ModbusRequest.Parse(register.ToArray()).Value);
        Assert.Throws<ModbusProtocolException>(() => ModbusRequest.Parse([0x05, 0, 1, 0x12, 0x34]));
    }

    [Fact]
    public void ReadRegisterResponseReturnsTypedRegisters()
    {
        ModbusRequest request = ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 2);
        ModbusResponse response = ModbusResponse.CreateReadRegistersResponse(request, [0x1122, 0x3344]);

        Assert.Equal("030411223344", Convert.ToHexString(response.ToArray()));
        ModbusResponse parsed = ModbusResponse.Parse(response.ToArray(), request);
        Assert.Equal([0x1122, 0x3344], parsed.GetRegisters());
    }

    [Fact]
    public void ReadCoilResponseReturnsRequestedBitCount()
    {
        ModbusRequest request = ModbusRequest.CreateRead(ModbusFunction.ReadCoils, 0, 10);
        bool[] values = [true, false, true, true, false, false, true, true, true, false];
        ModbusResponse response = ModbusResponse.CreateReadBitsResponse(request, values);

        Assert.Equal("0102CD01", Convert.ToHexString(response.ToArray()));
        Assert.Equal(values, ModbusResponse.Parse(response.ToArray(), request).GetBits());
    }

    [Fact]
    public void ExceptionResponseUsesFunctionHighBitAndStandardCode()
    {
        ModbusRequest request = ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1);
        ModbusResponse response = ModbusResponse.CreateExceptionResponse(
            request,
            ModbusExceptionCode.IllegalDataAddress);

        Assert.Equal("8302", Convert.ToHexString(response.ToArray()));
        ModbusResponse parsed = ModbusResponse.Parse(response.ToArray(), request);
        Assert.True(parsed.IsException);
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, parsed.ExceptionCode);
    }

    [Fact]
    public void WriteResponseRejectsMismatchedEcho()
    {
        ModbusRequest request = ModbusRequest.CreateWriteSingleRegister(1, 2);

        Assert.Throws<ModbusProtocolException>(() =>
            ModbusResponse.Parse([0x06, 0x00, 0x02, 0x00, 0x02], request));
    }

    [Fact]
    public void RequestValidationRejectsInvalidQuantitiesAndAddressOverflow()
    {
        Assert.Throws<ModbusProtocolException>(() =>
            ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 0));
        Assert.Throws<ModbusProtocolException>(() =>
            ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 126));
        Assert.Throws<ModbusProtocolException>(() =>
            ModbusRequest.CreateWriteMultipleRegisters(0, new ushort[124]));

        ModbusProtocolException exception = Assert.Throws<ModbusProtocolException>(() =>
            ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0xFFFF, 2));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, exception.ExceptionCode);
    }

    [Fact]
    public void RequestParserRejectsUnknownFunctionAndWrongByteCount()
    {
        ModbusProtocolException unknown = Assert.Throws<ModbusProtocolException>(() =>
            ModbusRequest.Parse([0x20, 0, 0, 0, 1]));
        Assert.Equal(ModbusExceptionCode.IllegalFunction, unknown.ExceptionCode);

        Assert.Throws<ModbusProtocolException>(() =>
            ModbusRequest.Parse([0x10, 0, 0, 0, 2, 3, 0, 1, 0]));
    }
}
