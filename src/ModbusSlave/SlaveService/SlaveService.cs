// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;

namespace ModbusSlave;

public sealed class SlaveService : IAsyncDisposable
{
    private readonly object _sync = new();
    private TcpServerTransport? _tcpServer;
    private SerialTransport? _serialTransport;
    private Channel<TransportDataEventArgs>? _serialQueue;
    private CancellationTokenSource? _serialCancellation;
    private Task? _serialWorker;
    private bool _disposed;

    public SlaveService(SlaveStationManager stations)
    {
        Processor = new SlaveRequestProcessor(stations ?? throw new ArgumentNullException(nameof(stations)));
    }

    public SlaveRequestProcessor Processor { get; }

    public SlaveFaultInjector FaultInjector { get; } = new();

    public bool IsTcpListening => _tcpServer?.IsListening == true;

    public bool IsSerialOpen => _serialTransport?.IsOpen == true;

    public IPEndPoint? TcpEndPoint => _tcpServer?.LocalEndPoint;

    public IReadOnlyCollection<TcpTransport> TcpConnections =>
        _tcpServer?.Connections ?? Array.Empty<TcpTransport>();

    public event EventHandler<TransportDataEventArgs>? FrameSent;

    public event EventHandler<TransportDataEventArgs>? FrameReceived;

    public event EventHandler<TransportErrorEventArgs>? Error;

    public event EventHandler<TransportConnectionEventArgs>? ClientConnected;

    public event EventHandler<TransportConnectionEventArgs>? ClientDisconnected;

    public event EventHandler<SlaveRequestEventArgs>? RequestProcessed;

    public async ValueTask StartTcpAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        TcpServerTransport server = new();
        server.FrameReceived += OnTcpFrameReceived;
        server.FrameSent += OnFrameSent;
        server.Error += OnTransportError;
        server.ClientConnected += OnClientConnected;
        server.ClientDisconnected += OnClientDisconnected;
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_tcpServer is not null)
            {
                throw new InvalidOperationException("The TCP slave is already listening.");
            }

            _tcpServer = server;
        }

        try
        {
            await server.StartAsync(address, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                _tcpServer = null;
            }

            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StartSerialAsync(
        SerialTransportSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SerialTransport transport = new(settings, ModbusRtuFrameKind.Request);
        Channel<TransportDataEventArgs> queue = Channel.CreateUnbounded<TransportDataEventArgs>();
        CancellationTokenSource cancellation = new();
        transport.FrameReceived += OnSerialFrameReceived;
        transport.FrameSent += OnFrameSent;
        transport.Error += OnTransportError;
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_serialTransport is not null)
            {
                throw new InvalidOperationException("The serial slave is already open.");
            }

            _serialTransport = transport;
            _serialQueue = queue;
            _serialCancellation = cancellation;
        }

        try
        {
            await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            _serialWorker = Task.Run(() => SerialWorkerAsync(transport, queue, cancellation.Token));
        }
        catch
        {
            lock (_sync)
            {
                _serialTransport = null;
                _serialQueue = null;
                _serialCancellation = null;
            }

            cancellation.Dispose();
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync()
    {
        TcpServerTransport? server;
        SerialTransport? serial;
        Channel<TransportDataEventArgs>? queue;
        CancellationTokenSource? serialCancellation;
        Task? serialWorker;
        lock (_sync)
        {
            server = _tcpServer;
            serial = _serialTransport;
            queue = _serialQueue;
            serialCancellation = _serialCancellation;
            serialWorker = _serialWorker;
            _tcpServer = null;
            _serialTransport = null;
            _serialQueue = null;
            _serialCancellation = null;
            _serialWorker = null;
        }

        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        if (serial is not null)
        {
            serialCancellation?.Cancel();
            queue?.Writer.TryComplete();
            await serial.DisposeAsync().ConfigureAwait(false);
            if (serialWorker is not null)
            {
                await serialWorker.ConfigureAwait(false);
            }

            serialCancellation?.Dispose();
        }
    }

    public async ValueTask<int> SendRawAsync(
        ReadOnlyMemory<byte> data,
        bool sendToTcp = true,
        bool sendToSerial = true,
        CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        TcpServerTransport? server;
        SerialTransport? serial;
        string[] connectionIds;
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            server = sendToTcp ? _tcpServer : null;
            serial = sendToSerial ? _serialTransport : null;
            connectionIds = server?.Connections.Select(connection => connection.ConnectionId).ToArray() ?? [];
        }

        int sentCount = 0;
        if (serial is not null)
        {
            try
            {
                await serial.SendAsync(data, cancellationToken).ConfigureAwait(false);
                sentCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RaiseError(exception, serial.ConnectionId);
            }
        }

        if (server is not null)
        {
            foreach (string connectionId in connectionIds)
            {
                try
                {
                    await server.SendAsync(connectionId, data, cancellationToken).ConfigureAwait(false);
                    sentCount++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    RaiseError(exception, connectionId);
                }
            }
        }

        return sentCount;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        Processor.Stations.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void OnTcpFrameReceived(object? sender, TransportDataEventArgs args)
    {
        RaiseFrameReceived(args);
        try
        {
            ModbusTcpFrame frame = ModbusTcpFrame.Parse(args.Data.Span);
            long startedAt = Stopwatch.GetTimestamp();
            SlaveRequestResult? result = Processor.Process(frame.UnitId, frame.Pdu.Span);
            if (result is null)
            {
                return;
            }

            result = FaultInjector.Apply(result, isRtu: false);
            if (result.ResponseDelay > TimeSpan.Zero)
            {
                await Task.Delay(result.ResponseDelay).ConfigureAwait(false);
            }

            RaiseRequestProcessed(args.ConnectionId, result, ArgumentGuard.GetElapsedTime(startedAt));
            if (result.Response is not null && !result.IsDropped && sender is TcpServerTransport server)
            {
                byte[] response = ModbusTcpFrame.FromResponse(
                    frame.TransactionId,
                    frame.UnitId,
                    result.Response).ToArray();
                await server.SendAsync(args.ConnectionId!, response).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            RaiseError(exception, args.ConnectionId);
        }
    }

    private void OnSerialFrameReceived(object? sender, TransportDataEventArgs args)
    {
        RaiseFrameReceived(args);
        _serialQueue?.Writer.TryWrite(args);
    }

    private async Task SerialWorkerAsync(
        SerialTransport transport,
        Channel<TransportDataEventArgs> queue,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TransportDataEventArgs args in queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    ModbusRtuFrame frame = ModbusRtuFrame.Parse(args.Data.Span);
                    long startedAt = Stopwatch.GetTimestamp();
                    SlaveRequestResult? result = Processor.Process(frame.UnitId, frame.Pdu.Span);
                    if (result is null)
                    {
                        continue;
                    }

                    result = FaultInjector.Apply(result, isRtu: true);
                    if (result.ResponseDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(result.ResponseDelay, cancellationToken).ConfigureAwait(false);
                    }

                    RaiseRequestProcessed(args.ConnectionId, result, ArgumentGuard.GetElapsedTime(startedAt));
                    if (result.Response is not null && !result.IsDropped)
                    {
                        byte[] response = ModbusRtuFrame.FromResponse(
                            frame.UnitId,
                            result.Response).ToArray();
                        if (result.Faults.HasFlag(SlaveFaultKind.InvalidCrc))
                        {
                            response[^1] ^= 0x01;
                        }

                        await transport.SendAsync(response, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    RaiseError(exception, args.ConnectionId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnFrameSent(object? sender, TransportDataEventArgs args)
    {
        try
        {
            FrameSent?.Invoke(this, args);
        }
        catch
        {
        }
    }

    private void OnTransportError(object? sender, TransportErrorEventArgs args)
    {
        RaiseError(args.Exception, args.ConnectionId);
    }

    private void OnClientConnected(object? sender, TransportConnectionEventArgs args)
    {
        try
        {
            ClientConnected?.Invoke(this, args);
        }
        catch
        {
        }
    }

    private void OnClientDisconnected(object? sender, TransportConnectionEventArgs args)
    {
        try
        {
            ClientDisconnected?.Invoke(this, args);
        }
        catch
        {
        }
    }

    private void RaiseFrameReceived(TransportDataEventArgs args)
    {
        try
        {
            FrameReceived?.Invoke(this, args);
        }
        catch
        {
        }
    }

    private void RaiseRequestProcessed(
        string? connectionId,
        SlaveRequestResult result,
        TimeSpan processingTime)
    {
        try
        {
            RequestProcessed?.Invoke(this, new SlaveRequestEventArgs(connectionId, result, processingTime));
        }
        catch
        {
        }
    }

    private void RaiseError(Exception exception, string? connectionId)
    {
        try
        {
            Error?.Invoke(this, new TransportErrorEventArgs(exception, connectionId));
        }
        catch
        {
        }
    }
}
