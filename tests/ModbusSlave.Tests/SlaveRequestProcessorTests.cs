// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusDataModel;
using ModbusCore.ModbusProtocol;

namespace ModbusSlave.Tests;

public sealed class SlaveRequestProcessorTests
{
    [Fact]
    public void ReadFunctionsUseFourIndependentDataAreas()
    {
        using SlaveStationManager stations = new([1]);
        ModbusDataStore store = stations.GetOrAddStation(1);
        store.WriteCoils(5, [true]);
        store.WriteDiscreteInputs(5, [false]);
        store.WriteHoldingRegisters(5, [0x1111]);
        store.WriteInputRegisters(5, [0x2222]);
        SlaveRequestProcessor processor = new(stations);

        Assert.Equal(
            [true],
            Process(processor, ModbusRequest.CreateRead(ModbusFunction.ReadCoils, 5, 1)).GetBits());
        Assert.Equal(
            [false],
            Process(processor, ModbusRequest.CreateRead(ModbusFunction.ReadDiscreteInputs, 5, 1)).GetBits());
        Assert.Equal(
            [0x1111],
            Process(processor, ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 5, 1)).GetRegisters());
        Assert.Equal(
            [0x2222],
            Process(processor, ModbusRequest.CreateRead(ModbusFunction.ReadInputRegisters, 5, 1)).GetRegisters());
    }

    [Fact]
    public void AllWriteFunctionsUpdateDataAndReturnEchoes()
    {
        using SlaveStationManager stations = new([1]);
        ModbusDataStore store = stations.GetOrAddStation(1);
        SlaveRequestProcessor processor = new(stations);

        ModbusResponse singleCoil = Process(
            processor,
            ModbusRequest.CreateWriteSingleCoil(9, true));
        ModbusResponse singleRegister = Process(
            processor,
            ModbusRequest.CreateWriteSingleRegister(9, 0x1234));
        ModbusResponse multipleCoils = Process(
            processor,
            ModbusRequest.CreateWriteMultipleCoils(20, [true, false, true, true, false, true, false, true, true]));
        ModbusResponse multipleRegisters = Process(
            processor,
            ModbusRequest.CreateWriteMultipleRegisters(30, [0xAAAA, 0xBBBB, 0xCCCC]));

        Assert.True(store.ReadCoils(9, 1)[0]);
        Assert.Equal(0x1234, store.ReadHoldingRegisters(9, 1)[0]);
        Assert.Equal(
            [true, false, true, true, false, true, false, true, true],
            store.ReadCoils(20, 9));
        Assert.Equal([0xAAAA, 0xBBBB, 0xCCCC], store.ReadHoldingRegisters(30, 3));
        Assert.Equal(9, singleCoil.Address);
        Assert.Equal(0x1234, singleRegister.Value);
        Assert.Equal(9, multipleCoils.Quantity);
        Assert.Equal(3, multipleRegisters.Quantity);
    }

    [Theory]
    [InlineData("07", ModbusExceptionCode.IllegalFunction)]
    [InlineData("0300000000", ModbusExceptionCode.IllegalDataValue)]
    [InlineData("03FFFF0002", ModbusExceptionCode.IllegalDataAddress)]
    [InlineData("0F000000090100", ModbusExceptionCode.IllegalDataValue)]
    public void InvalidRequestsReturnStandardExceptionResponses(string pduHex, ModbusExceptionCode expected)
    {
        using SlaveStationManager stations = new([1]);
        SlaveRequestProcessor processor = new(stations);

        SlaveRequestResult result = processor.Process(1, Convert.FromHexString(pduHex))!;

        Assert.NotNull(result.Response);
        Assert.True(result.Response.IsException);
        Assert.Equal(expected, result.Response.ExceptionCode);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void BroadcastWritesAllStationsAndUnknownStationsAreSilent()
    {
        using SlaveStationManager stations = new([1, 2]);
        SlaveRequestProcessor processor = new(stations);
        ModbusRequest broadcast = ModbusRequest.CreateWriteSingleRegister(10, 0xBEEF);

        SlaveRequestResult broadcastResult = processor.Process(0, broadcast.ToArray())!;
        SlaveRequestResult? unknownResult = processor.Process(
            3,
            ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1).ToArray());

        Assert.True(broadcastResult.IsBroadcast);
        Assert.Null(broadcastResult.Response);
        Assert.Equal(0xBEEF, stations.GetOrAddStation(1).ReadHoldingRegisters(10, 1)[0]);
        Assert.Equal(0xBEEF, stations.GetOrAddStation(2).ReadHoldingRegisters(10, 1)[0]);
        Assert.Null(unknownResult);
    }

    private static ModbusResponse Process(SlaveRequestProcessor processor, ModbusRequest request)
    {
        SlaveRequestResult result = processor.Process(1, request.ToArray())
            ?? throw new InvalidOperationException("The station did not respond.");
        Assert.False(result.Response!.IsException);
        return result.Response;
    }
}
