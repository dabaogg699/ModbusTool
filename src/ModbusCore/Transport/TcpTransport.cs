// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Net;
using System.Net.Sockets;
using ModbusCore.ModbusProtocol;

namespace ModbusCore.Transport;

public sealed class TcpTransport : IModbusTransport
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _sync = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private string? _remoteEndPoint;
    private bool _connecting;
    private bool _disposed;

    public TcpTransport()
    {
        ConnectionId = Guid.NewGuid().ToString("N");
    }

    public string ConnectionId { get; }

    public string? RemoteEndPoint
    {
        get
        {
            lock (_sync)
            {
                return _remoteEndPoint;
            }
        }
    }

    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _client is not null;
            }
        }
    }

    public event EventHandler<TransportDataEventArgs>? FrameReceived;

    public event EventHandler<TransportDataEventArgs>? FrameSent;

    public event EventHandler<TransportErrorEventArgs>? Error;

    public event EventHandler<TransportConnectionEventArgs>? Disconnected;

    public async ValueTask ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(host);
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_client is not null || _connecting)
            {
                throw new InvalidOperationException("The TCP transport is already connected or connecting.");
            }

            _connecting = true;
        }

        TcpClient client = new() { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            AttachClient(client, true);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _connecting = false;
            }
        }
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        NetworkStream stream = GetOpenStream();
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(stream, GetOpenStream()))
            {
                throw new InvalidOperationException("The TCP connection was closed before sending.");
            }

            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            FrameSent?.Invoke(this, new TransportDataEventArgs(frame, ConnectionId));
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask CloseAsync()
    {
        TcpClient? client;
        NetworkStream? stream;
        CancellationTokenSource? cancellation;
        Task? receiveTask;
        lock (_sync)
        {
            client = _client;
            stream = _stream;
            cancellation = _receiveCancellation;
            receiveTask = _receiveTask;
            _client = null;
            _stream = null;
            _receiveCancellation = null;
            _receiveTask = null;
            _remoteEndPoint = null;
        }

        if (client is null)
        {
            return;
        }

        string? remoteEndPoint = client.Client.RemoteEndPoint?.ToString();
        cancellation?.Cancel();
        stream?.Dispose();
        client.Dispose();
        if (receiveTask is not null)
        {
            await receiveTask.ConfigureAwait(false);
        }

        cancellation?.Dispose();
        Disconnected?.Invoke(this, new TransportConnectionEventArgs(ConnectionId, remoteEndPoint));
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _disposed = true;
        }

        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static TcpTransport CreateAccepted(TcpClient client)
    {
        TcpTransport transport = new();
        transport.AttachClient(client, false);
        return transport;
    }

    internal void StartReceiving()
    {
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_client is null || _stream is null || _receiveCancellation is null)
            {
                throw new InvalidOperationException("No TCP client is attached.");
            }

            if (_receiveTask is null)
            {
                _receiveTask = Task.Run(() => ReceiveLoopAsync(
                    _client,
                    _stream,
                    _receiveCancellation.Token));
            }
        }
    }

    private void AttachClient(TcpClient client, bool startReceiving)
    {
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_client is not null)
            {
                throw new InvalidOperationException("A TCP client is already attached.");
            }

            _client = client;
            _stream = client.GetStream();
            _receiveCancellation = new CancellationTokenSource();
            _remoteEndPoint = client.Client.RemoteEndPoint?.ToString();
        }

        if (startReceiving)
        {
            StartReceiving();
        }
    }

    private NetworkStream GetOpenStream()
    {
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            return _stream ?? throw new InvalidOperationException("The TCP transport is not connected.");
        }
    }

    private async Task ReceiveLoopAsync(
        TcpClient client,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        ModbusTcpFrameParser parser = new();
        byte[] buffer = new byte[16384];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                parser.Append(buffer.AsSpan(0, bytesRead));
                while (true)
                {
                    ModbusTcpFrame? frame;
                    try
                    {
                        if (!parser.TryReadFrame(out frame))
                        {
                            break;
                        }
                    }
                    catch (ModbusProtocolException exception)
                    {
                        Error?.Invoke(this, new TransportErrorEventArgs(exception, ConnectionId));
                        continue;
                    }

                    FrameReceived?.Invoke(this, new TransportDataEventArgs(frame!.ToArray(), ConnectionId));
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error?.Invoke(this, new TransportErrorEventArgs(exception, ConnectionId));
        }
        finally
        {
            CompleteUnexpectedDisconnect(client);
        }
    }

    private void CompleteUnexpectedDisconnect(TcpClient client)
    {
        NetworkStream? stream = null;
        CancellationTokenSource? cancellation = null;
        string? remoteEndPoint = null;
        bool notify = false;
        lock (_sync)
        {
            if (ReferenceEquals(_client, client))
            {
                stream = _stream;
                cancellation = _receiveCancellation;
                remoteEndPoint = _remoteEndPoint;
                _client = null;
                _stream = null;
                _receiveCancellation = null;
                _receiveTask = null;
                _remoteEndPoint = null;
                notify = true;
            }
        }

        if (notify)
        {
            stream?.Dispose();
            client.Dispose();
            cancellation?.Dispose();
            Disconnected?.Invoke(this, new TransportConnectionEventArgs(ConnectionId, remoteEndPoint));
        }
    }
}
