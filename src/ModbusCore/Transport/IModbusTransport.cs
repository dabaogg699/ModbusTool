// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusCore.Transport;

public interface IModbusTransport : IAsyncDisposable
{
    string ConnectionId { get; }

    string? RemoteEndPoint { get; }

    bool IsOpen { get; }

    event EventHandler<TransportDataEventArgs>? FrameReceived;

    event EventHandler<TransportDataEventArgs>? FrameSent;

    event EventHandler<TransportErrorEventArgs>? Error;

    event EventHandler<TransportConnectionEventArgs>? Disconnected;

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);

    ValueTask CloseAsync();
}
