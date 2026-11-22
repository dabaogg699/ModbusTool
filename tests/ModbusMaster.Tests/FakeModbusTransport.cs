// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Threading.Channels;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;

namespace ModbusMaster.Tests;

internal sealed class FakeModbusTransport : IModbusTransport
{
    public string ConnectionId => "fake";

    public string? RemoteEndPoint => "loopback";

    public bool IsOpen { get; private set; }

    public int OpenCount { get; private set; }

    public int CloseCount { get; private set; }

    public Channel<byte[]> SentFrames { get; } = Channel.CreateUnbounded<byte[]>();

    public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? SendHandler { get; set; }

    public event EventHandler<TransportDataEventArgs>? FrameReceived;

    public event EventHandler<TransportDataEventArgs>? FrameSent;

    public event EventHandler<TransportErrorEventArgs>? Error;

    public event EventHandler<TransportConnectionEventArgs>? Disconnected;

    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsOpen)
        {
            throw new InvalidOperationException("The fake transport is already open.");
        }

        IsOpen = true;
        OpenCount++;
        return ValueTask.CompletedTask;
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOpen)
        {
            throw new InvalidOperationException("The fake transport is closed.");
        }

        SentFrames.Writer.TryWrite(frame.ToArray());
        FrameSent?.Invoke(this, new TransportDataEventArgs(frame, ConnectionId));
        if (SendHandler is not null)
        {
            await SendHandler(frame, cancellationToken);
        }
    }

    public void Emit(ReadOnlyMemory<byte> frame)
    {
        FrameReceived?.Invoke(this, new TransportDataEventArgs(frame, ConnectionId));
    }

    public void RespondWithRegisters(ReadOnlyMemory<byte> requestBytes, params ushort[] registers)
    {
        ModbusTcpFrame requestFrame = ModbusTcpFrame.Parse(requestBytes.Span);
        ModbusRequest request = ModbusRequest.Parse(requestFrame.Pdu.Span);
        ModbusResponse response = ModbusResponse.CreateReadRegistersResponse(request, registers);
        Emit(ModbusTcpFrame.FromResponse(
            requestFrame.TransactionId,
            requestFrame.UnitId,
            response).ToArray());
    }

    public ValueTask CloseAsync()
    {
        if (!IsOpen)
        {
            return ValueTask.CompletedTask;
        }

        IsOpen = false;
        CloseCount++;
        Disconnected?.Invoke(this, new TransportConnectionEventArgs(ConnectionId, RemoteEndPoint));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _ = Error;
    }
}
