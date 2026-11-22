// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusSlave;

public sealed class SlaveRequestEventArgs : EventArgs
{
    internal SlaveRequestEventArgs(
        string? connectionId,
        SlaveRequestResult result,
        TimeSpan processingTime)
    {
        ConnectionId = connectionId;
        Result = result;
        ProcessingTime = processingTime;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public string? ConnectionId { get; }

    public SlaveRequestResult Result { get; }

    public TimeSpan ProcessingTime { get; }

    public DateTimeOffset Timestamp { get; }
}
