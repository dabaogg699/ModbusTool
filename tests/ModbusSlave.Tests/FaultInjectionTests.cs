// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusProtocol;

namespace ModbusSlave.Tests;

public sealed class FaultInjectionTests
{
    [Fact]
    public void DropAndDelayAreReportedWithoutChangingOriginalResponse()
    {
        using SlaveStationManager stations = new([1]);
        SlaveRequestResult original = ProcessRead(stations);
        SlaveFaultInjector injector = new()
        {
            Settings = new FaultInjectionSettings
            {
                Enabled = true,
                DropProbability = 1,
                DelayMilliseconds = 25
            }
        };

        SlaveRequestResult result = injector.Apply(original, isRtu: true);

        Assert.True(result.IsDropped);
        Assert.Equal(TimeSpan.FromMilliseconds(25), result.ResponseDelay);
        Assert.Equal(
            SlaveFaultKind.Dropped | SlaveFaultKind.Delayed,
            result.Faults);
        Assert.Same(original.Response, result.Response);
    }

    [Fact]
    public void InvalidCrcOnlyAppliesToRtuResponses()
    {
        using SlaveStationManager stations = new([1]);
        SlaveFaultInjector injector = new()
        {
            Settings = new FaultInjectionSettings { Enabled = true, InvalidCrcProbability = 1 }
        };

        SlaveRequestResult tcp = injector.Apply(ProcessRead(stations), isRtu: false);
        SlaveRequestResult rtu = injector.Apply(ProcessRead(stations), isRtu: true);

        Assert.False(tcp.Faults.HasFlag(SlaveFaultKind.InvalidCrc));
        Assert.True(rtu.Faults.HasFlag(SlaveFaultKind.InvalidCrc));
    }

    [Fact]
    public void InvalidValuePreservesResponseShapeButChangesReturnedData()
    {
        using SlaveStationManager stations = new([1]);
        stations.GetOrAddStation(1).WriteSingleRegister(0, 0x1234);
        SlaveFaultInjector injector = new()
        {
            Settings = new FaultInjectionSettings { Enabled = true, InvalidValueProbability = 1 }
        };

        SlaveRequestResult result = injector.Apply(ProcessRead(stations), isRtu: false);

        Assert.True(result.Faults.HasFlag(SlaveFaultKind.InvalidValue));
        Assert.Equal([0xEDCB], result.Response!.GetRegisters());
    }

    [Fact]
    public void ForcedExceptionTakesPrecedenceOverInvalidValue()
    {
        using SlaveStationManager stations = new([1]);
        SlaveFaultInjector injector = new()
        {
            Settings = new FaultInjectionSettings
            {
                Enabled = true,
                InvalidValueProbability = 1,
                ForcedExceptionCode = ModbusExceptionCode.SlaveDeviceBusy
            }
        };

        SlaveRequestResult result = injector.Apply(ProcessRead(stations), isRtu: false);

        Assert.True(result.Response!.IsException);
        Assert.Equal(ModbusExceptionCode.SlaveDeviceBusy, result.Response.ExceptionCode);
        Assert.Equal(SlaveFaultKind.ForcedException, result.Faults);
    }

    [Fact]
    public void InvalidWriteResponseChangesTheEchoAddress()
    {
        using SlaveStationManager stations = new([1]);
        SlaveRequestProcessor processor = new(stations);
        SlaveRequestResult original = processor.Process(
            1,
            ModbusRequest.CreateWriteSingleRegister(9, 0x1234).ToArray())!;
        SlaveFaultInjector injector = new()
        {
            Settings = new FaultInjectionSettings { Enabled = true, InvalidValueProbability = 1 }
        };

        SlaveRequestResult result = injector.Apply(original, isRtu: false);

        Assert.Equal(8, result.Response!.Address);
        Assert.Equal(0x1234, result.Response.Value);
        Assert.Equal(0x1234, stations.GetOrAddStation(1).ReadHoldingRegisters(9, 1)[0]);
    }

    private static SlaveRequestResult ProcessRead(SlaveStationManager stations)
    {
        SlaveRequestProcessor processor = new(stations);
        return processor.Process(
            1,
            ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1).ToArray())!;
    }
}
