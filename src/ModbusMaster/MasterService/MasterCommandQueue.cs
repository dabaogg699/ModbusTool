// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusProtocol;

namespace ModbusMaster;

internal sealed class QueuedMasterCommand
{
    public required byte UnitId { get; init; }

    public required ModbusRequest Request { get; init; }

    public required MasterCommandPriority Priority { get; init; }

    public required TimeSpan ResponseTimeout { get; init; }

    public required int RetryCount { get; init; }

    public PollSchedule? Schedule { get; init; }

    public CancellationToken CancellationToken { get; init; }

    public TaskCompletionSource<ModbusResponse> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>有界优先队列：手动命令优先于轮询，同优先级按入队顺序执行，停止时统一取消未处理命令。</summary>
internal sealed class MasterCommandQueue : IDisposable
{
    private readonly PriorityQueue<QueuedMasterCommand, long> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _sync = new();
    private readonly int _capacity;
    private long _sequence;
    private bool _disposed;

    public MasterCommandQueue(int capacity)
    {
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    public bool TryEnqueue(QueuedMasterCommand command)
    {
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_queue.Count >= _capacity)
            {
                return false;
            }

            long priority = ((long)command.Priority << 32) | (uint)_sequence++;
            _queue.Enqueue(command, priority);
        }

        _signal.Release();
        return true;
    }

    public async ValueTask<QueuedMasterCommand> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                ArgumentGuard.ThrowIfDisposed(_disposed, this);
                if (_queue.TryDequeue(out QueuedMasterCommand? command, out _))
                {
                    return command;
                }
            }
        }
    }

    public void CancelAll(CancellationToken cancellationToken)
    {
        List<QueuedMasterCommand> commands = [];
        lock (_sync)
        {
            while (_queue.TryDequeue(out QueuedMasterCommand? command, out _))
            {
                commands.Add(command);
            }
        }

        while (_signal.Wait(0))
        {
        }

        foreach (QueuedMasterCommand command in commands)
        {
            command.Completion.TrySetCanceled(cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }

        _signal.Dispose();
    }
}
