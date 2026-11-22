// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Text;
using System.Threading.Channels;

namespace ModbusCore.Common;

/// <summary>
/// 有界异步日志服务。按日期与文件大小滚动，保留数量可配置；
/// 协议线程只负责入队，磁盘写入、滚动和清理由单一后台任务串行完成。
/// </summary>
public sealed class RollingFileLogService : IAsyncDisposable
{
    private readonly Channel<LogRequest> _channel;
    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly Task _worker;
    private int _disposed;
    private int _retainedFileCount;
    private long _maxFileSizeBytes;
    private long _droppedCount;
    private volatile bool _verboseEnabled;

    public RollingFileLogService(
        string directory,
        string filePrefix = "ModbusTool",
        int capacity = 10000,
        long maxFileSizeBytes = 5 * 1024 * 1024,
        int retainedFileCount = 5)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(directory);
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePrefix);
        ArgumentGuard.ThrowIfLessThan(capacity, 100);
        ArgumentGuard.ThrowIfLessThanOrEqual(maxFileSizeBytes, 0);
        ArgumentGuard.ThrowIfLessThan(retainedFileCount, 1);
        _directory = Path.GetFullPath(directory);
        _filePrefix = filePrefix;
        _maxFileSizeBytes = maxFileSizeBytes;
        _retainedFileCount = retainedFileCount;
        _channel = Channel.CreateBounded<LogRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessAsync);
    }

    public event Action<ToolLogEntry>? EntryWritten;

    public event Action<Exception>? Error;

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>调试级日志开关，默认关闭；Release 与 Debug 构建均不隐式开启。</summary>
    public bool VerboseEnabled
    {
        get => _verboseEnabled;
        set => _verboseEnabled = value;
    }

    /// <summary>单个日志文件的最大字节数，新阈值从下一次写入判断开始生效。</summary>
    public long MaxFileSizeBytes
    {
        get => Volatile.Read(ref _maxFileSizeBytes);
        set
        {
            ArgumentGuard.ThrowIfLessThanOrEqual(value, 0);
            Volatile.Write(ref _maxFileSizeBytes, value);
        }
    }

    /// <summary>保留的日志文件总数（包含当前文件），下次打开文件时执行清理。</summary>
    public int RetainedFileCount
    {
        get => Volatile.Read(ref _retainedFileCount);
        set
        {
            ArgumentGuard.ThrowIfLessThan(value, 1);
            Volatile.Write(ref _retainedFileCount, value);
        }
    }

    public string? LastError { get; private set; }

    public void Write(
        ToolLogLevel level,
        string category,
        string message,
        Exception? exception = null,
        DateTimeOffset? timestamp = null,
        ToolLogContext? context = null)
    {
        string? details = exception is null
            ? null
            : level == ToolLogLevel.Error
                ? exception.ToString()
                : exception.Message;
        Write(level, category, message, details, timestamp, context);
    }

    public void Write(
        ToolLogLevel level,
        string category,
        string message,
        string? details,
        DateTimeOffset? timestamp = null,
        ToolLogContext? context = null)
    {
        ArgumentGuard.ThrowIfDisposed(Volatile.Read(ref _disposed) != 0, this);
        ArgumentGuard.ThrowIfNullOrWhiteSpace(category);
        ArgumentGuard.ThrowIfNullOrWhiteSpace(message);
        if (level == ToolLogLevel.Debug && !_verboseEnabled)
        {
            return;
        }

        ToolLogEntry entry = new(timestamp ?? DateTimeOffset.Now, level, category.Trim(), message.Trim(), details, context);
        if (!_channel.Writer.TryWrite(new LogRequest(entry)))
        {
            Interlocked.Increment(ref _droppedCount);
            return;
        }

        try
        {
            EntryWritten?.Invoke(entry);
        }
        catch
        {
            // A log viewer must never interrupt protocol processing.
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfDisposed(Volatile.Read(ref _disposed) != 0, this);
        LogRequest request = LogRequest.CreateFlush();
        await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await request.Completion!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        StreamWriter? writer = null;
        DateOnly? currentDate = null;
        string? currentFilePath = null;
        int currentIndex = 0;
        long currentLength = 0;
        try
        {
            await foreach (LogRequest request in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (request.Entry is null)
                {
                    try
                    {
                        if (writer is not null)
                        {
                            await writer.FlushAsync().ConfigureAwait(false);
                        }

                        request.Completion!.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        request.Completion!.TrySetException(exception);
                        ReportError(exception);
                    }

                    continue;
                }

                try
                {
                    DateOnly entryDate = DateOnly.FromDateTime(request.Entry.Timestamp.LocalDateTime);
                    if (writer is null || currentDate != entryDate)
                    {
                        if (writer is not null)
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                        }

                        currentDate = entryDate;
                        currentIndex = FindLatestIndex(entryDate);
                        string candidate = GetFilePath(entryDate, currentIndex);
                        if (File.Exists(candidate) && new FileInfo(candidate).Length >= MaxFileSizeBytes)
                        {
                            currentIndex++;
                        }

                        currentFilePath = GetFilePath(entryDate, currentIndex);
                        writer = CreateWriter(currentFilePath, out currentLength);
                        CleanupRetainedFiles(currentFilePath);
                    }

                    string text = request.Entry.ToLogLine() + Environment.NewLine;
                    long byteCount = Encoding.UTF8.GetByteCount(text);
                    if (currentLength > 0 && currentLength + byteCount > MaxFileSizeBytes)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                        currentIndex++;
                        currentFilePath = GetFilePath(entryDate, currentIndex);
                        writer = CreateWriter(currentFilePath, out currentLength);
                        CleanupRetainedFiles(currentFilePath);
                    }

                    await writer.WriteAsync(text).ConfigureAwait(false);
                    currentLength += byteCount;
                    LastError = null;
                }
                catch (Exception exception)
                {
                    ReportError(exception);
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                        writer = null;
                        currentDate = null;
                        currentFilePath = null;
                        currentLength = 0;
                    }
                }
            }

            if (writer is not null)
            {
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private StreamWriter CreateWriter(string filePath, out long length)
    {
        Directory.CreateDirectory(_directory);
        FileStream stream = new(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        length = stream.Length;
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
    }

    private int FindLatestIndex(DateOnly date)
    {
        int index = 0;
        while (File.Exists(GetFilePath(date, index + 1)))
        {
            index++;
        }

        return index;
    }

    private string GetFilePath(DateOnly date, int index)
    {
        string suffix = index == 0 ? string.Empty : $"-{index:D3}";
        return Path.Combine(_directory, $"{_filePrefix}-{date:yyyy-MM-dd}{suffix}.log");
    }

    private void CleanupRetainedFiles(string activeFilePath)
    {
        int retainedCount = RetainedFileCount;
        HashSet<string> retained = new(StringComparer.OrdinalIgnoreCase) { activeFilePath };
        foreach (string path in Directory.GetFiles(_directory, $"{_filePrefix}-*.log")
                     .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                     .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (retained.Count >= retainedCount)
            {
                break;
            }

            retained.Add(path);
        }

        foreach (string path in Directory.GetFiles(_directory, $"{_filePrefix}-*.log"))
        {
            if (retained.Contains(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception exception)
            {
                ReportError(exception);
            }
        }
    }

    private void ReportError(Exception exception)
    {
        LastError = exception.Message;
        try
        {
            Error?.Invoke(exception);
        }
        catch
        {
            // Logging errors cannot be reported recursively through the same service.
        }
    }

    private sealed class LogRequest
    {
        public LogRequest(ToolLogEntry entry)
        {
            Entry = entry;
        }

        private LogRequest()
        {
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ToolLogEntry? Entry { get; }

        public TaskCompletionSource? Completion { get; }

        public static LogRequest CreateFlush() => new();
    }
}
