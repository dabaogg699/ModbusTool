// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusDataModel;
using ModbusCore.ModbusProtocol;

namespace ModbusSlave;

public sealed class SlaveRequestResult
{
    internal SlaveRequestResult(
        byte unitId,
        ModbusRequest? request,
        ModbusResponse? response,
        bool isBroadcast,
        Exception? error,
        SlaveFaultKind faults = SlaveFaultKind.None,
        TimeSpan responseDelay = default)
    {
        UnitId = unitId;
        Request = request;
        Response = response;
        IsBroadcast = isBroadcast;
        Error = error;
        Faults = faults;
        ResponseDelay = responseDelay;
    }

    public byte UnitId { get; }

    public ModbusRequest? Request { get; }

    public ModbusResponse? Response { get; }

    public bool IsBroadcast { get; }

    public Exception? Error { get; }

    public SlaveFaultKind Faults { get; }

    public TimeSpan ResponseDelay { get; }

    public bool IsDropped => Faults.HasFlag(SlaveFaultKind.Dropped);

    public bool IsFaultInjected => Faults != SlaveFaultKind.None;

    internal SlaveRequestResult WithFaults(
        SlaveFaultKind faults,
        TimeSpan responseDelay,
        ModbusResponse? response)
    {
        return new SlaveRequestResult(
            UnitId,
            Request,
            response,
            IsBroadcast,
            Error,
            faults,
            responseDelay);
    }
}

public sealed class SlaveRequestProcessor
{
    public SlaveRequestProcessor(SlaveStationManager stations)
    {
        Stations = stations ?? throw new ArgumentNullException(nameof(stations));
    }

    public SlaveStationManager Stations { get; }

    public SlaveRequestResult? Process(byte unitId, ReadOnlySpan<byte> pdu)
    {
        if (unitId == 0)
        {
            return ProcessBroadcast(pdu);
        }

        if (!Stations.TryGetStation(unitId, out ModbusDataStore? store))
        {
            return null;
        }

        if (pdu.IsEmpty)
        {
            ModbusProtocolException exception = new("The request PDU is empty.");
            return new SlaveRequestResult(unitId, null, null, false, exception);
        }

        ModbusFunction function = (ModbusFunction)pdu[0];
        try
        {
            ModbusRequest request = ModbusRequest.Parse(pdu);
            ModbusResponse response = ProcessRequest(store!, request);
            return new SlaveRequestResult(unitId, request, response, false, null);
        }
        catch (ModbusProtocolException exception)
        {
            return CreateException(unitId, function, exception.ExceptionCode, exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return CreateException(unitId, function, ModbusExceptionCode.IllegalDataAddress, exception);
        }
        catch (Exception exception)
        {
            return CreateException(unitId, function, ModbusExceptionCode.SlaveDeviceFailure, exception);
        }
    }

    private SlaveRequestResult ProcessBroadcast(ReadOnlySpan<byte> pdu)
    {
        try
        {
            ModbusRequest request = ModbusRequest.Parse(pdu);
            if (request.Function is not (ModbusFunction.WriteSingleCoil
                or ModbusFunction.WriteSingleRegister
                or ModbusFunction.WriteMultipleCoils
                or ModbusFunction.WriteMultipleRegisters))
            {
                throw new ModbusProtocolException("Broadcast requests must be write requests.");
            }

            foreach (byte unitId in Stations.UnitIds)
            {
                if (Stations.TryGetStation(unitId, out ModbusDataStore? store))
                {
                    _ = ProcessRequest(store!, request);
                }
            }

            return new SlaveRequestResult(0, request, null, true, null);
        }
        catch (Exception exception)
        {
            return new SlaveRequestResult(0, null, null, true, exception);
        }
    }

    private static ModbusResponse ProcessRequest(ModbusDataStore store, ModbusRequest request)
    {
        return request.Function switch
        {
            ModbusFunction.ReadCoils => ModbusResponse.CreateReadBitsResponse(
                request,
                store.ReadCoils(request.Address, request.Quantity)),
            ModbusFunction.ReadDiscreteInputs => ModbusResponse.CreateReadBitsResponse(
                request,
                store.ReadDiscreteInputs(request.Address, request.Quantity)),
            ModbusFunction.ReadHoldingRegisters => ModbusResponse.CreateReadRegistersResponse(
                request,
                store.ReadHoldingRegisters(request.Address, request.Quantity)),
            ModbusFunction.ReadInputRegisters => ModbusResponse.CreateReadRegistersResponse(
                request,
                store.ReadInputRegisters(request.Address, request.Quantity)),
            ModbusFunction.WriteSingleCoil => WriteSingleCoil(store, request),
            ModbusFunction.WriteSingleRegister => WriteSingleRegister(store, request),
            ModbusFunction.WriteMultipleCoils => WriteMultipleCoils(store, request),
            ModbusFunction.WriteMultipleRegisters => WriteMultipleRegisters(store, request),
            _ => throw new ModbusProtocolException(
                "The function is not supported.",
                ModbusExceptionCode.IllegalFunction)
        };
    }

    private static ModbusResponse WriteSingleCoil(ModbusDataStore store, ModbusRequest request)
    {
        store.WriteSingleCoil(request.Address, request.SingleCoilValue);
        return ModbusResponse.CreateWriteResponse(request);
    }

    private static ModbusResponse WriteSingleRegister(ModbusDataStore store, ModbusRequest request)
    {
        store.WriteSingleRegister(request.Address, request.Value);
        return ModbusResponse.CreateWriteResponse(request);
    }

    private static ModbusResponse WriteMultipleCoils(ModbusDataStore store, ModbusRequest request)
    {
        store.WriteCoils(request.Address, request.Coils.Span);
        return ModbusResponse.CreateWriteResponse(request);
    }

    private static ModbusResponse WriteMultipleRegisters(ModbusDataStore store, ModbusRequest request)
    {
        store.WriteHoldingRegisters(request.Address, request.Registers.Span);
        return ModbusResponse.CreateWriteResponse(request);
    }

    private static SlaveRequestResult CreateException(
        byte unitId,
        ModbusFunction function,
        ModbusExceptionCode code,
        Exception error)
    {
        return new SlaveRequestResult(
            unitId,
            null,
            ModbusResponse.CreateExceptionResponse(function, code),
            false,
            error);
    }
}
