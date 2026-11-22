// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusMaster;

public sealed record TrendSample(DateTimeOffset Timestamp, double? Value, bool IsBreak);

public sealed class TrendSampleEventArgs : EventArgs
{
    internal TrendSampleEventArgs(Guid pointId, TrendSample sample)
    {
        PointId = pointId;
        Sample = sample;
    }

    public Guid PointId { get; }

    public TrendSample Sample { get; }
}

/// <summary>
/// 线程安全的趋势采样缓冲区：按点位压缩重复值、保留恒值心跳与断点，
/// 在锁内维护有界历史，在锁外发布采样事件，避免通信线程触碰 UI。
/// </summary>
public sealed class TrendService
{
    private readonly Dictionary<Guid, TrendSeries> _series = [];
    private readonly object _sync = new();
    private readonly int _seriesCapacity;

    public TrendService(int seriesCapacity = 2000)
    {
        if (seriesCapacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(seriesCapacity));
        }

        _seriesCapacity = seriesCapacity;
    }

    public event EventHandler<TrendSampleEventArgs>? SampleRecorded;

    public void Record(Guid pointId, double value, DateTimeOffset? timestamp = null)
    {
        TrendSample? sample = null;
        lock (_sync)
        {
            TrendSeries series = GetOrCreateSeries(pointId);
            if (series.TryAddValue(value, timestamp ?? DateTimeOffset.UtcNow, out sample))
            {
                Trim(series);
            }
        }

        if (sample is not null)
        {
            SampleRecorded?.Invoke(this, new TrendSampleEventArgs(pointId, sample));
        }
    }

    public void MarkBreak(Guid pointId, DateTimeOffset? timestamp = null)
    {
        TrendSample? sample;
        lock (_sync)
        {
            TrendSeries series = GetOrCreateSeries(pointId);
            sample = series.AddBreak(timestamp ?? DateTimeOffset.UtcNow);
            Trim(series);
        }

        if (sample is not null)
        {
            SampleRecorded?.Invoke(this, new TrendSampleEventArgs(pointId, sample));
        }
    }

    public IReadOnlyList<TrendSample> GetSnapshot(Guid pointId)
    {
        lock (_sync)
        {
            return _series.TryGetValue(pointId, out TrendSeries? series)
                ? series.Samples.ToArray()
                : [];
        }
    }

    public void Clear(Guid pointId)
    {
        lock (_sync)
        {
            _series.Remove(pointId);
        }
    }

    private TrendSeries GetOrCreateSeries(Guid pointId)
    {
        if (!_series.TryGetValue(pointId, out TrendSeries? series))
        {
            series = new TrendSeries();
            _series.Add(pointId, series);
        }

        return series;
    }

    private void Trim(TrendSeries series)
    {
        while (series.Samples.Count > _seriesCapacity)
        {
            series.Samples.Dequeue();
        }
    }

    private sealed class TrendSeries
    {
        // 同值采样默认压缩，但超过心跳间隔仍需补点，否则恒定值曲线不延伸、时间轴停滞。
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

        private double? _lastValue;
        private DateTimeOffset? _lastTimestamp;
        private bool _needsValueAfterBreak;

        public Queue<TrendSample> Samples { get; } = new();

        public bool TryAddValue(double value, DateTimeOffset timestamp, out TrendSample? sample)
        {
            bool heartbeatDue = !_lastTimestamp.HasValue
                || timestamp - _lastTimestamp.Value >= HeartbeatInterval;
            if (!_needsValueAfterBreak && !heartbeatDue
                && _lastValue.HasValue && _lastValue.Value.Equals(value))
            {
                sample = null;
                return false;
            }

            sample = new TrendSample(timestamp, value, false);
            Samples.Enqueue(sample);
            _lastValue = value;
            _lastTimestamp = timestamp;
            _needsValueAfterBreak = false;
            return true;
        }

        public TrendSample? AddBreak(DateTimeOffset timestamp)
        {
            _needsValueAfterBreak = true;
            if (Samples.Count > 0 && Samples.Last().IsBreak)
            {
                return null;
            }

            TrendSample sample = new(timestamp, null, true);
            Samples.Enqueue(sample);
            return sample;
        }
    }
}
