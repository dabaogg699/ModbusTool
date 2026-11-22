// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusMaster;

public sealed record MasterServiceOptions
{
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public int RetryCount { get; init; } = 2;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    public int QueueCapacity { get; init; } = 1000;

    internal void Validate()
    {
        if (ResponseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ResponseTimeout));
        }

        if (RetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryCount));
        }

        if (RetryDelay < TimeSpan.Zero || ReconnectDelay < TimeSpan.Zero || FrameInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "Transport delays cannot be negative.");
        }

        if (QueueCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        }
    }
}

public enum MasterCommandPriority
{
    Manual = 0,
    Poll = 1
}
