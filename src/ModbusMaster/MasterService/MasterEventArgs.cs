// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusProtocol;

namespace ModbusMaster;

public sealed record MasterPointValue(
    Guid PointId,
    string Name,
    object RawValue,
    double ScaledValue,
    string Unit,
    DateTimeOffset Timestamp);

public sealed class MasterResponseEventArgs : EventArgs
{
    internal MasterResponseEventArgs(
        PollSchedule? schedule,
        byte unitId,
        ModbusRequest request,
        ModbusResponse response,
        TimeSpan responseTime,
        bool isPoll,
        IReadOnlyList<MasterPointValue> pointValues)
    {
        Schedule = schedule;
        UnitId = unitId;
        Request = request;
        Response = response;
        ResponseTime = responseTime;
        IsPoll = isPoll;
        PointValues = pointValues;
    }

    public PollSchedule? Schedule { get; }

    public byte UnitId { get; }

    public ModbusRequest Request { get; }

    public ModbusResponse Response { get; }

    public TimeSpan ResponseTime { get; }

    public bool IsPoll { get; }

    public IReadOnlyList<MasterPointValue> PointValues { get; }
}

public sealed class MasterErrorEventArgs : EventArgs
{
    internal MasterErrorEventArgs(
        Exception exception,
        byte unitId,
        ModbusRequest request,
        bool isPoll,
        bool willRetry,
        PollSchedule? schedule = null)
    {
        Exception = exception;
        UnitId = unitId;
        Request = request;
        IsPoll = isPoll;
        WillRetry = willRetry;
        Schedule = schedule;
    }

    public Exception Exception { get; }

    public byte UnitId { get; }

    public ModbusRequest Request { get; }

    public bool IsPoll { get; }

    public bool WillRetry { get; }

    public PollSchedule? Schedule { get; }
}
