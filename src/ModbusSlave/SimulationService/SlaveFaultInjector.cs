// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusProtocol;

namespace ModbusSlave;

public sealed class SlaveFaultInjector
{
    private FaultInjectionSettings _settings = new();

    public FaultInjectionSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ValidateProbability(value.DropProbability, nameof(value.DropProbability));
            ValidateProbability(value.InvalidValueProbability, nameof(value.InvalidValueProbability));
            ValidateProbability(value.InvalidCrcProbability, nameof(value.InvalidCrcProbability));
            if (value.DelayMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The response delay cannot be negative.");
            }

            if (value.ForcedExceptionCode is ModbusExceptionCode code && !Enum.IsDefined(code))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The forced exception code is invalid.");
            }

            Volatile.Write(ref _settings, value);
        }
    }

    public SlaveRequestResult Apply(SlaveRequestResult result, bool isRtu)
    {
        ArgumentNullException.ThrowIfNull(result);
        FaultInjectionSettings settings = Settings;
        if (!settings.Enabled || result.IsBroadcast || result.Response is null)
        {
            return result;
        }

        SlaveFaultKind faults = settings.DelayMilliseconds > 0 ? SlaveFaultKind.Delayed : SlaveFaultKind.None;
        TimeSpan delay = TimeSpan.FromMilliseconds(settings.DelayMilliseconds);
        if (Roll(settings.DropProbability))
        {
            return result.WithFaults(faults | SlaveFaultKind.Dropped, delay, result.Response);
        }

        ModbusResponse response = result.Response;
        if (settings.ForcedExceptionCode is ModbusExceptionCode exceptionCode)
        {
            response = ModbusResponse.CreateExceptionResponse(response.Function, exceptionCode);
            faults |= SlaveFaultKind.ForcedException;
        }
        else if (!response.IsException && Roll(settings.InvalidValueProbability))
        {
            ModbusResponse? invalidResponse = CreateInvalidValueResponse(result);
            if (invalidResponse is not null)
            {
                response = invalidResponse;
                faults |= SlaveFaultKind.InvalidValue;
            }
        }

        if (isRtu && Roll(settings.InvalidCrcProbability))
        {
            faults |= SlaveFaultKind.InvalidCrc;
        }

        return result.WithFaults(faults, delay, response);
    }

    private static ModbusResponse? CreateInvalidValueResponse(SlaveRequestResult result)
    {
        ModbusRequest? request = result.Request;
        ModbusResponse response = result.Response!;
        if (request is null)
        {
            return null;
        }

        switch (request.Function)
        {
            case ModbusFunction.ReadCoils:
            case ModbusFunction.ReadDiscreteInputs:
                bool[] bits = response.GetBits();
                bits[0] = !bits[0];
                return ModbusResponse.CreateReadBitsResponse(request, bits);
            case ModbusFunction.ReadHoldingRegisters:
            case ModbusFunction.ReadInputRegisters:
                ushort[] registers = response.GetRegisters();
                registers[^1] ^= 0xFFFF;
                return ModbusResponse.CreateReadRegistersResponse(request, registers);
            case ModbusFunction.WriteSingleCoil:
                return ModbusResponse.CreateWriteResponse(ModbusRequest.CreateWriteSingleCoil(
                    (ushort)(request.Address ^ 1), request.SingleCoilValue));
            case ModbusFunction.WriteSingleRegister:
                return ModbusResponse.CreateWriteResponse(ModbusRequest.CreateWriteSingleRegister(
                    (ushort)(request.Address ^ 1), request.Value));
            case ModbusFunction.WriteMultipleCoils:
                return ModbusResponse.CreateWriteResponse(ModbusRequest.CreateWriteMultipleCoils(
                    (ushort)(request.Address ^ 1), request.Coils.Span));
            case ModbusFunction.WriteMultipleRegisters:
                return ModbusResponse.CreateWriteResponse(ModbusRequest.CreateWriteMultipleRegisters(
                    (ushort)(request.Address ^ 1), request.Registers.Span));
            default:
                return null;
        }
    }

    private static bool Roll(double probability) => Random.Shared.NextDouble() < probability;

    private static void ValidateProbability(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(name, "A probability must be between zero and one.");
        }
    }
}
