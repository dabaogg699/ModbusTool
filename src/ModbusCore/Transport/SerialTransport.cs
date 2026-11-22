// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.IO.Ports;
using ModbusCore.ModbusProtocol;

namespace ModbusCore.Transport;

public sealed class SerialTransport : IModbusTransport
{
    private readonly SerialTransportSettings _settings;
    private readonly ModbusRtuFrameKind _frameKind;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _sync = new();
    private SerialPort? _port;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private bool _disposed;

    public SerialTransport(SerialTransportSettings settings, ModbusRtuFrameKind frameKind)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            throw new ArgumentException("A serial port name is required.", nameof(settings));
        }

        _frameKind = frameKind;
        _ = settings.GetInterFrameDelay();
    }

    public string ConnectionId => _settings.PortName;

    public string? RemoteEndPoint => null;

    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _port?.IsOpen == true;
            }
        }
    }

    public event EventHandler<TransportDataEventArgs>? FrameReceived;

    public event EventHandler<TransportDataEventArgs>? FrameSent;

    public event EventHandler<TransportErrorEventArgs>? Error;

    public event EventHandler<TransportConnectionEventArgs>? Disconnected;

    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_port is not null)
            {
                throw new InvalidOperationException("The serial transport is already open.");
            }

            SerialPort port = new(_settings.PortName)
            {
                BaudRate = _settings.BaudRate,
                Parity = _settings.Parity,
                DataBits = _settings.DataBits,
                StopBits = _settings.StopBits,
                Handshake = _settings.Handshake
            };

            try
            {
                port.Open();
                CancellationTokenSource cancellation = new();
                _port = port;
                _receiveCancellation = cancellation;
                _receiveTask = Task.Run(() => ReceiveLoopAsync(port, cancellation.Token));
            }
            catch
            {
                port.Dispose();
                _port = null;
                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        SerialPort port = GetOpenPort();
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(port, GetOpenPort()) || !port.IsOpen)
            {
                throw new InvalidOperationException("The serial port was closed before sending.");
            }

            await port.BaseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            FrameSent?.Invoke(this, new TransportDataEventArgs(frame, ConnectionId));
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask CloseAsync()
    {
        SerialPort? port;
        CancellationTokenSource? cancellation;
        Task? receiveTask;
        lock (_sync)
        {
            port = _port;
            cancellation = _receiveCancellation;
            receiveTask = _receiveTask;
            _port = null;
            _receiveCancellation = null;
            _receiveTask = null;
        }

        if (port is null)
        {
            return;
        }

        cancellation?.Cancel();
        port.Close();
        port.Dispose();
        if (receiveTask is not null)
        {
            await receiveTask.ConfigureAwait(false);
        }

        cancellation?.Dispose();
        Disconnected?.Invoke(this, new TransportConnectionEventArgs(ConnectionId, RemoteEndPoint));
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

    private SerialPort GetOpenPort()
    {
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            return _port is { IsOpen: true } port
                ? port
                : throw new InvalidOperationException("The serial transport is not open.");
        }
    }

    private async Task ReceiveLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        ModbusRtuFrameParser parser = new(_frameKind);
        byte[] buffer = new byte[1024];
        TimeSpan interFrameDelay = _settings.GetInterFrameDelay();
        DateTimeOffset lastReceivedAt = default;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan readTimeout = Timeout.InfiniteTimeSpan;
                if (parser.BufferedByteCount > 0)
                {
                    readTimeout = interFrameDelay - (DateTimeOffset.UtcNow - lastReceivedAt);
                    if (readTimeout <= TimeSpan.Zero)
                    {
                        CompleteFrameAfterIdle(parser);
                        continue;
                    }
                }

                int bytesRead;
                try
                {
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    timeout.CancelAfter(readTimeout);
                    bytesRead = await port.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && parser.BufferedByteCount > 0)
                {
                    CompleteFrameAfterIdle(parser);
                    continue;
                }

                if (bytesRead == 0)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                parser.Append(buffer.AsSpan(0, bytesRead));
                lastReceivedAt = DateTimeOffset.UtcNow;
                ReadAvailableFrames(parser);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Error?.Invoke(this, new TransportErrorEventArgs(exception, ConnectionId));
        }
        finally
        {
            CompleteUnexpectedDisconnect(port);
        }
    }

    private void ReadAvailableFrames(ModbusRtuFrameParser parser)
    {
        while (true)
        {
            ModbusRtuFrame? frame;
            try
            {
                if (!parser.TryReadFrame(out frame))
                {
                    return;
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

    private void CompleteFrameAfterIdle(ModbusRtuFrameParser parser)
    {
        ModbusRtuFrame frame;
        try
        {
            frame = parser.ReadFrameAfterIdle();
        }
        catch (ModbusProtocolException exception)
        {
            Error?.Invoke(this, new TransportErrorEventArgs(exception, ConnectionId));
            return;
        }

        FrameReceived?.Invoke(this, new TransportDataEventArgs(frame.ToArray(), ConnectionId));
    }

    private void CompleteUnexpectedDisconnect(SerialPort port)
    {
        CancellationTokenSource? cancellation = null;
        bool notify = false;
        lock (_sync)
        {
            if (ReferenceEquals(_port, port))
            {
                _port = null;
                cancellation = _receiveCancellation;
                _receiveCancellation = null;
                _receiveTask = null;
                notify = true;
            }
        }

        if (notify)
        {
            port.Dispose();
            cancellation?.Dispose();
            Disconnected?.Invoke(this, new TransportConnectionEventArgs(ConnectionId, RemoteEndPoint));
        }
    }
}
