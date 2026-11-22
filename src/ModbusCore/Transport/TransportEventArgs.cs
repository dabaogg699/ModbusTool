// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusCore.Transport;

public class TransportDataEventArgs : EventArgs
{
    public TransportDataEventArgs(ReadOnlyMemory<byte> data, string? connectionId = null)
    {
        Data = data.ToArray();
        ConnectionId = connectionId;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public ReadOnlyMemory<byte> Data { get; }

    public string? ConnectionId { get; }

    public DateTimeOffset Timestamp { get; }
}

public class TransportErrorEventArgs : EventArgs
{
    public TransportErrorEventArgs(Exception exception, string? connectionId = null)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        ConnectionId = connectionId;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public Exception Exception { get; }

    public string? ConnectionId { get; }

    public DateTimeOffset Timestamp { get; }
}

public class TransportConnectionEventArgs : EventArgs
{
    public TransportConnectionEventArgs(string connectionId, string? remoteEndPoint)
    {
        ConnectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
        RemoteEndPoint = remoteEndPoint;
    }

    public string ConnectionId { get; }

    public string? RemoteEndPoint { get; }
}
