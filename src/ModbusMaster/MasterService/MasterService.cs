// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Diagnostics;
using System.Globalization;
using ModbusCore.ByteConvert;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;

namespace ModbusMaster;

/// <summary>
/// 主站通信编排核心：串行执行优先级命令队列、匹配请求/应答、处理超时重试与断线重连，
/// 并将轮询结果转换为点位采样。传输与协议编解码均由构造函数注入，不依赖 WPF。
/// </summary>
public sealed class MasterService : IAsyncDisposable
{
    private readonly IModbusTransport _transport;
    private readonly IMasterFrameCodec _codec;
    private readonly Func<CancellationToken, ValueTask> _connectAsync;
    private readonly MasterServiceOptions _options;
    private readonly MasterCommandQueue _queue;
    private readonly MasterStatistics _statistics = new();
    private readonly object _sync = new();
    private readonly object _pendingSync = new();
    private IReadOnlyList<PollSchedule> _schedules = [];
    private CancellationTokenSource? _lifetime;
    private Task? _workerTask;
    private Task[] _pollTasks = [];
    private PendingExchange? _pending;
    private ushort _transactionId;
    private bool _hasConnected;
    private DateTimeOffset _nextReconnectAt;
    private bool _disposed;

    public MasterService(
        IModbusTransport transport,
        IMasterFrameCodec codec,
        Func<CancellationToken, ValueTask> connectAsync,
        MasterServiceOptions? options = null,
        TrendService? trendService = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
        _options = options ?? new MasterServiceOptions();
        _options.Validate();
        TrendService = trendService;
        _queue = new MasterCommandQueue(_options.QueueCapacity);
        _transport.FrameSent += OnTransportFrameSent;
        _transport.FrameReceived += OnTransportFrameReceived;
        _transport.Disconnected += OnTransportDisconnected;
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _lifetime is not null;
            }
        }
    }

    public int QueuedCommandCount => _queue.Count;

    public TrendService? TrendService { get; }

    public event EventHandler<TransportDataEventArgs>? FrameSent;

    public event EventHandler<TransportDataEventArgs>? FrameReceived;

    public event EventHandler<MasterResponseEventArgs>? ResponseReceived;

    public event EventHandler<MasterErrorEventArgs>? CommandFailed;

    public MasterStatisticsSnapshot GetStatistics() => _statistics.Snapshot();

    /// <summary>以不可变快照配置周期轮询；运行中禁止替换，避免调度任务与 UI 配置互相竞争。</summary>
    public void ConfigurePolling(IEnumerable<PollSchedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        PollSchedule[] snapshot = schedules.ToArray();
        foreach (PollSchedule schedule in snapshot)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            if (schedule.Interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(schedules), "Polling intervals must be positive.");
            }
        }

        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_lifetime is not null)
            {
                throw new InvalidOperationException("Stop the master before replacing polling schedules.");
            }

            _schedules = snapshot;
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_lifetime is not null)
            {
                throw new InvalidOperationException("The master service is already running.");
            }

            CancellationTokenSource lifetime = new();
            _lifetime = lifetime;
            _nextReconnectAt = DateTimeOffset.MinValue;
            _workerTask = Task.Run(() => WorkerLoopAsync(lifetime.Token));
            _pollTasks = _schedules
                .Select(schedule => Task.Run(() => PollLoopAsync(schedule, lifetime.Token)))
                .ToArray();
        }

        return ValueTask.CompletedTask;
    }

    public async Task<ModbusResponse> ExecuteAsync(
        byte unitId,
        ModbusRequest request,
        TimeSpan? responseTimeout = null,
        int? retryCount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        QueuedMasterCommand command = EnqueueCommand(
            unitId,
            request,
            MasterCommandPriority.Manual,
            null,
            responseTimeout,
            retryCount,
            cancellationToken) ?? throw new InvalidOperationException("The manual command was not queued.");
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => command.Completion.TrySetCanceled(cancellationToken));
        return await command.Completion.Task.ConfigureAwait(false);
    }

    public async ValueTask StopAsync()
    {
        CancellationTokenSource? lifetime;
        Task? worker;
        Task[] polls;
        lock (_sync)
        {
            lifetime = _lifetime;
            worker = _workerTask;
            polls = _pollTasks;
            _lifetime = null;
            _workerTask = null;
            _pollTasks = [];
        }

        if (lifetime is null)
        {
            return;
        }

        lifetime.Cancel();
        _queue.CancelAll(lifetime.Token);
        lock (_pendingSync)
        {
            _pending?.Completion.TrySetCanceled(lifetime.Token);
        }

        await CloseTransportAfterFailure().ConfigureAwait(false);
        List<Task> tasks = polls.ToList();
        if (worker is not null)
        {
            tasks.Add(worker);
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        lifetime.Dispose();
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
        _transport.FrameSent -= OnTransportFrameSent;
        _transport.FrameReceived -= OnTransportFrameReceived;
        _transport.Disconnected -= OnTransportDisconnected;
        _queue.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private QueuedMasterCommand? EnqueueCommand(
        byte unitId,
        ModbusRequest request,
        MasterCommandPriority priority,
        PollSchedule? schedule,
        TimeSpan? responseTimeout,
        int? retryCount,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = responseTimeout ?? schedule?.ResponseTimeout ?? _options.ResponseTimeout;
        int retries = retryCount ?? schedule?.RetryCount ?? _options.RetryCount;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }

        if (retries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        }

        QueuedMasterCommand command = new()
        {
            UnitId = unitId,
            Request = request,
            Priority = priority,
            Schedule = schedule,
            ResponseTimeout = timeout,
            RetryCount = retries,
            CancellationToken = cancellationToken
        };

        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_lifetime is null)
            {
                throw new InvalidOperationException("Start the master service before sending commands.");
            }

            if (!_queue.TryEnqueue(command))
            {
                if (priority == MasterCommandPriority.Poll)
                {
                    _statistics.IncrementDroppedPolls();
                    return null;
                }

                throw new InvalidOperationException("The master command queue is full.");
            }
        }

        return command;
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                QueuedMasterCommand command = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
                if (command.Completion.Task.IsCompleted)
                {
                    continue;
                }

                using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    command.CancellationToken);
                try
                {
                    ModbusResponse response = await ExecuteWithRetries(command, operation.Token)
                        .ConfigureAwait(false);
                    command.Completion.TrySetResult(response);
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested)
                {
                    CancellationToken canceledToken = command.CancellationToken.IsCancellationRequested
                        ? command.CancellationToken
                        : cancellationToken;
                    command.Completion.TrySetCanceled(canceledToken);
                }
                catch (Exception exception)
                {
                    command.Completion.TrySetException(exception);
                }

                if (_options.FrameInterval > TimeSpan.Zero)
                {
                    await Task.Delay(_options.FrameInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<ModbusResponse> ExecuteWithRetries(
        QueuedMasterCommand command,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= command.RetryCount; attempt++)
        {
            if (attempt > 0)
            {
                _statistics.IncrementRetries();
                if (_options.RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                return await ExchangeOnce(command, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (exception is TimeoutException)
                {
                    _statistics.IncrementTimeouts();
                }
                else
                {
                    _statistics.IncrementErrors();
                }

                bool willRetry = attempt < command.RetryCount;
                RaiseCommandFailed(command, exception, willRetry);
                await CloseTransportAfterFailure().ConfigureAwait(false);
                if (!willRetry)
                {
                    MarkHistoryBreak(command.Schedule);
                    throw;
                }
            }
        }

        throw new InvalidOperationException("The retry loop exited unexpectedly.");
    }

    private async Task<ModbusResponse> ExchangeOnce(
        QueuedMasterCommand command,
        CancellationToken cancellationToken)
    {
        await EnsureConnected(cancellationToken).ConfigureAwait(false);
        ushort transactionId = unchecked(++_transactionId);
        PendingExchange pending = new(command, transactionId);
        lock (_pendingSync)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("Another Modbus exchange is already pending.");
            }

            _pending = pending;
        }

        try
        {
            byte[] frame = _codec.Encode(command.UnitId, transactionId, command.Request);
            await _transport.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            ExchangeResult result = await pending.Completion.Task
                .WaitAsync(command.ResponseTimeout, cancellationToken)
                .ConfigureAwait(false);
            _statistics.IncrementCompleted();
            _statistics.RecordResponse(result.ResponseTime);
            if (result.Response.IsException)
            {
                _statistics.IncrementErrors();
                MarkHistoryBreak(command.Schedule);
            }
            else
            {
                RecordHistory(command.Schedule, result.PointValues);
            }

            RaiseResponseReceived(command, result);
            return result.Response;
        }
        finally
        {
            lock (_pendingSync)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }
        }
    }

    private async Task EnsureConnected(CancellationToken cancellationToken)
    {
        if (_transport.IsOpen)
        {
            return;
        }

        TimeSpan reconnectWait = _nextReconnectAt - DateTimeOffset.UtcNow;
        if (reconnectWait > TimeSpan.Zero)
        {
            await Task.Delay(reconnectWait, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _connectAsync(cancellationToken).ConfigureAwait(false);
            if (_hasConnected)
            {
                _statistics.IncrementReconnects();
            }

            _hasConnected = true;
            _nextReconnectAt = DateTimeOffset.MinValue;
        }
        catch
        {
            _nextReconnectAt = DateTimeOffset.UtcNow + _options.ReconnectDelay;
            throw;
        }
    }

    private async Task CloseTransportAfterFailure()
    {
        if (_transport.IsOpen)
        {
            _nextReconnectAt = DateTimeOffset.UtcNow + _options.ReconnectDelay;
            try
            {
                await _transport.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task PollLoopAsync(PollSchedule schedule, CancellationToken cancellationToken)
    {
        try
        {
            if (schedule.StartDelay > TimeSpan.Zero)
            {
                await Task.Delay(schedule.StartDelay, cancellationToken).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                long startedAt = Stopwatch.GetTimestamp();
                try
                {
                    QueuedMasterCommand? command = EnqueueCommand(
                        schedule.UnitId,
                        schedule.Request,
                        MasterCommandPriority.Poll,
                        schedule,
                        schedule.ResponseTimeout,
                        schedule.RetryCount,
                        cancellationToken);
                    if (command is not null)
                    {
                        _ = await command.Completion.Task.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                }

                TimeSpan delay = schedule.Interval - ArgumentGuard.GetElapsedTime(startedAt);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnTransportFrameSent(object? sender, TransportDataEventArgs args)
    {
        _statistics.IncrementSent();
        try
        {
            FrameSent?.Invoke(this, args);
        }
        catch
        {
        }
    }

    private void OnTransportFrameReceived(object? sender, TransportDataEventArgs args)
    {
        _statistics.IncrementReceived();
        try
        {
            FrameReceived?.Invoke(this, args);
        }
        catch
        {
        }

        PendingExchange? pending;
        lock (_pendingSync)
        {
            pending = _pending;
        }

        if (pending is null)
        {
            return;
        }

        try
        {
            ModbusResponse? response = _codec.Decode(
                args.Data.Span,
                pending.Command.UnitId,
                pending.TransactionId,
                pending.Command.Request);
            if (response is not null)
            {
                ExchangeResult result = new(
                    response,
                    ArgumentGuard.GetElapsedTime(pending.StartedAt),
                    DecodePointValues(pending.Command.Schedule, response));
                pending.Completion.TrySetResult(result);
            }
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetException(exception);
        }
    }

    private void OnTransportDisconnected(object? sender, TransportConnectionEventArgs args)
    {
        lock (_pendingSync)
        {
            _pending?.Completion.TrySetException(new IOException("The Modbus connection was disconnected."));
        }
    }

    private static IReadOnlyList<MasterPointValue> DecodePointValues(
        PollSchedule? schedule,
        ModbusResponse response)
    {
        if (schedule?.Block is null || response.IsException
            || response.Function is not (ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters))
        {
            return [];
        }

        ushort[] registers = response.GetRegisters();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        List<MasterPointValue> values = [];
        foreach (MasterDataPoint point in schedule.Block.Points)
        {
            int count = RegisterConverter.GetRegisterCount(point.DataType);
            object rawValue = RegisterConverter.Decode(
                registers.AsSpan(point.AddressOffset, count),
                point.DataType,
                point.ByteOrder);
            double scaledValue = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture) * point.Scale
                + point.Offset;
            values.Add(new MasterPointValue(
                point.Id,
                point.Name,
                rawValue,
                scaledValue,
                point.Unit,
                timestamp));
        }

        return values;
    }

    private void RecordHistory(PollSchedule? schedule, IReadOnlyList<MasterPointValue> values)
    {
        if (TrendService is null || schedule?.Block is null)
        {
            return;
        }

        try
        {
            Dictionary<Guid, MasterPointValue> valueMap = values.ToDictionary(value => value.PointId);
            foreach (MasterDataPoint point in schedule.Block.Points.Where(point => point.HistoryEnabled))
            {
                if (valueMap.TryGetValue(point.Id, out MasterPointValue? value))
                {
                    TrendService.Record(point.Id, value.ScaledValue, value.Timestamp);
                }
                else
                {
                    TrendService.MarkBreak(point.Id);
                }
            }
        }
        catch
        {
        }
    }

    private void MarkHistoryBreak(PollSchedule? schedule)
    {
        if (TrendService is null || schedule?.Block is null)
        {
            return;
        }

        try
        {
            foreach (MasterDataPoint point in schedule.Block.Points.Where(point => point.HistoryEnabled))
            {
                TrendService.MarkBreak(point.Id);
            }
        }
        catch
        {
        }
    }

    private void RaiseResponseReceived(QueuedMasterCommand command, ExchangeResult result)
    {
        try
        {
            ResponseReceived?.Invoke(this, new MasterResponseEventArgs(
                command.Schedule,
                command.UnitId,
                command.Request,
                result.Response,
                result.ResponseTime,
                command.Priority == MasterCommandPriority.Poll,
                result.PointValues));
        }
        catch
        {
        }
    }

    private void RaiseCommandFailed(QueuedMasterCommand command, Exception exception, bool willRetry)
    {
        try
        {
            CommandFailed?.Invoke(this, new MasterErrorEventArgs(
                exception,
                command.UnitId,
                command.Request,
                command.Priority == MasterCommandPriority.Poll,
                willRetry,
                command.Schedule));
        }
        catch
        {
        }
    }

    private sealed class PendingExchange
    {
        public PendingExchange(QueuedMasterCommand command, ushort transactionId)
        {
            Command = command;
            TransactionId = transactionId;
            StartedAt = Stopwatch.GetTimestamp();
        }

        public QueuedMasterCommand Command { get; }

        public ushort TransactionId { get; }

        public long StartedAt { get; }

        public TaskCompletionSource<ExchangeResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ExchangeResult(
        ModbusResponse Response,
        TimeSpan ResponseTime,
        IReadOnlyList<MasterPointValue> PointValues);
}
