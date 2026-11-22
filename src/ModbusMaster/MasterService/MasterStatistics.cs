// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusMaster;

public sealed record MasterStatisticsSnapshot(
    long SentFrames,
    long ReceivedFrames,
    long CompletedCommands,
    long SuccessfulExchanges,
    long Timeouts,
    long Errors,
    long Retries,
    long Reconnects,
    long DroppedPollCommands,
    double LastResponseMilliseconds,
    double AverageResponseMilliseconds);

internal sealed class MasterStatistics
{
    private long _sentFrames;
    private long _receivedFrames;
    private long _completedCommands;
    private long _successfulExchanges;
    private long _timeouts;
    private long _errors;
    private long _retries;
    private long _reconnects;
    private long _droppedPollCommands;
    private long _lastResponseTicks;
    private long _totalResponseTicks;

    public void IncrementSent() => Interlocked.Increment(ref _sentFrames);

    public void IncrementReceived() => Interlocked.Increment(ref _receivedFrames);

    public void IncrementCompleted() => Interlocked.Increment(ref _completedCommands);

    public void IncrementTimeouts() => Interlocked.Increment(ref _timeouts);

    public void IncrementErrors() => Interlocked.Increment(ref _errors);

    public void IncrementRetries() => Interlocked.Increment(ref _retries);

    public void IncrementReconnects() => Interlocked.Increment(ref _reconnects);

    public void IncrementDroppedPolls() => Interlocked.Increment(ref _droppedPollCommands);

    public void RecordResponse(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _successfulExchanges);
        Interlocked.Exchange(ref _lastResponseTicks, elapsed.Ticks);
        Interlocked.Add(ref _totalResponseTicks, elapsed.Ticks);
    }

    public MasterStatisticsSnapshot Snapshot()
    {
        long successfulExchanges = Interlocked.Read(ref _successfulExchanges);
        long totalTicks = Interlocked.Read(ref _totalResponseTicks);
        return new MasterStatisticsSnapshot(
            Interlocked.Read(ref _sentFrames),
            Interlocked.Read(ref _receivedFrames),
            Interlocked.Read(ref _completedCommands),
            successfulExchanges,
            Interlocked.Read(ref _timeouts),
            Interlocked.Read(ref _errors),
            Interlocked.Read(ref _retries),
            Interlocked.Read(ref _reconnects),
            Interlocked.Read(ref _droppedPollCommands),
            TimeSpan.FromTicks(Interlocked.Read(ref _lastResponseTicks)).TotalMilliseconds,
            successfulExchanges == 0
                ? 0
                : TimeSpan.FromTicks(totalTicks).TotalMilliseconds / successfulExchanges);
    }
}
