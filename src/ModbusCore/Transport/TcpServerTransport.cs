// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ModbusCore.Transport;

public sealed class TcpServerTransport : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TcpTransport> _connections = new();
    private readonly object _sync = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCancellation;
    private Task? _acceptTask;
    private bool _disposed;

    public IPEndPoint? LocalEndPoint { get; private set; }

    public bool IsListening
    {
        get
        {
            lock (_sync)
            {
                return _listener is not null;
            }
        }
    }

    public IReadOnlyCollection<TcpTransport> Connections => _connections.Values.ToArray();

    public event EventHandler<TransportDataEventArgs>? FrameReceived;

    public event EventHandler<TransportDataEventArgs>? FrameSent;

    public event EventHandler<TransportErrorEventArgs>? Error;

    public event EventHandler<TransportConnectionEventArgs>? ClientConnected;

    public event EventHandler<TransportConnectionEventArgs>? ClientDisconnected;

    public ValueTask StartAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_listener is not null)
            {
                throw new InvalidOperationException("The TCP server is already listening.");
            }

            TcpListener listener = new(address, port);
            listener.Start();
            CancellationTokenSource cancellation = new();
            _listener = listener;
            _acceptCancellation = cancellation;
            LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, cancellation.Token));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendAsync(
        string connectionId,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(connectionId);
        if (!_connections.TryGetValue(connectionId, out TcpTransport? connection))
        {
            throw new KeyNotFoundException($"TCP connection '{connectionId}' does not exist.");
        }

        return connection.SendAsync(frame, cancellationToken);
    }

    public async ValueTask StopAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cancellation;
        Task? acceptTask;
        lock (_sync)
        {
            listener = _listener;
            cancellation = _acceptCancellation;
            acceptTask = _acceptTask;
            _listener = null;
            _acceptCancellation = null;
            _acceptTask = null;
            LocalEndPoint = null;
        }

        if (listener is not null)
        {
            cancellation?.Cancel();
            listener.Stop();
            if (acceptTask is not null)
            {
                await acceptTask.ConfigureAwait(false);
            }

            cancellation?.Dispose();
        }

        foreach (TcpTransport connection in _connections.Values.ToArray())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                TcpTransport connection = TcpTransport.CreateAccepted(client);
                connection.FrameReceived += OnConnectionFrameReceived;
                connection.FrameSent += OnConnectionFrameSent;
                connection.Error += OnConnectionError;
                connection.Disconnected += OnConnectionDisconnected;
                _connections[connection.ConnectionId] = connection;
                ClientConnected?.Invoke(
                    this,
                    new TransportConnectionEventArgs(connection.ConnectionId, connection.RemoteEndPoint));
                connection.StartReceiving();
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error?.Invoke(this, new TransportErrorEventArgs(exception));
        }
        finally
        {
            CompleteUnexpectedStop(listener);
        }
    }

    private void OnConnectionFrameReceived(object? sender, TransportDataEventArgs args)
    {
        FrameReceived?.Invoke(this, args);
    }

    private void OnConnectionFrameSent(object? sender, TransportDataEventArgs args)
    {
        FrameSent?.Invoke(this, args);
    }

    private void OnConnectionError(object? sender, TransportErrorEventArgs args)
    {
        Error?.Invoke(this, args);
    }

    private void OnConnectionDisconnected(object? sender, TransportConnectionEventArgs args)
    {
        if (_connections.TryRemove(args.ConnectionId, out TcpTransport? connection))
        {
            ClientDisconnected?.Invoke(this, args);
        }
    }

    private void CompleteUnexpectedStop(TcpListener listener)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_listener, listener))
            {
                return;
            }

            _listener = null;
            _acceptCancellation?.Dispose();
            _acceptCancellation = null;
            _acceptTask = null;
            LocalEndPoint = null;
        }

        listener.Stop();
    }
}
