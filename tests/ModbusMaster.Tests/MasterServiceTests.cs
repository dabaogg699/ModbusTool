// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.Concurrent;
using ModbusCore.ByteConvert;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;

namespace ModbusMaster.Tests;

public sealed class MasterServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ManualRequestReturnsResponseAndUpdatesStatistics()
    {
        FakeModbusTransport transport = new();
        transport.SendHandler = (frame, _) =>
        {
            transport.RespondWithRegisters(frame, 0x1234);
            return ValueTask.CompletedTask;
        };
        MasterServiceOptions options = FastOptions(retryCount: 0);
        await using MasterService service = new(
            transport,
            new TcpMasterFrameCodec(),
            transport.OpenAsync,
            options);
        TaskCompletionSource<MasterResponseEventArgs> responseReceived = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ResponseReceived += (_, args) => responseReceived.TrySetResult(args);
        await service.StartAsync();
        ModbusRequest request = ReadRequest();

        ModbusResponse response = await service.ExecuteAsync(1, request).WaitAsync(TestTimeout);
        MasterResponseEventArgs responseEvent = await responseReceived.Task.WaitAsync(TestTimeout);
        MasterStatisticsSnapshot statistics = service.GetStatistics();

        Assert.Equal(0x1234, Assert.Single(response.GetRegisters()));
        Assert.False(responseEvent.IsPoll);
        Assert.True(responseEvent.ResponseTime >= TimeSpan.Zero);
        Assert.Equal(1, statistics.SentFrames);
        Assert.Equal(1, statistics.ReceivedFrames);
        Assert.Equal(1, statistics.CompletedCommands);
        Assert.Equal(0, statistics.Reconnects);
        Assert.Equal(responseEvent.ResponseTime.TotalMilliseconds, statistics.LastResponseMilliseconds, 6);
        Assert.Equal(responseEvent.ResponseTime.TotalMilliseconds, statistics.AverageResponseMilliseconds, 6);
        await service.StopAsync();
    }

    [Fact]
    public async Task ManualCommandPreemptsQueuedPollingCommand()
    {
        FakeModbusTransport transport = new();
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sendCount = 0;
        transport.SendHandler = async (frame, cancellationToken) =>
        {
            int current = Interlocked.Increment(ref sendCount);
            if (current == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            transport.RespondWithRegisters(frame, (ushort)current);
        };
        await using MasterService service = new(
            transport,
            new TcpMasterFrameCodec(),
            transport.OpenAsync,
            FastOptions(retryCount: 0));
        PollSchedule firstPoll = CreatePollSchedule();
        PollSchedule secondPoll = CreatePollSchedule();
        service.ConfigurePolling([firstPoll, secondPoll]);
        ConcurrentQueue<string> completionOrder = new();
        TaskCompletionSource allCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ResponseReceived += (_, args) =>
        {
            completionOrder.Enqueue(args.IsPoll ? args.Schedule!.BlockId.ToString() : "manual");
            if (completionOrder.Count == 3)
            {
                allCompleted.TrySetResult();
            }
        };
        await service.StartAsync();
        await firstStarted.Task.WaitAsync(TestTimeout);
        await WaitUntil(() => service.QueuedCommandCount == 1);

        Task<ModbusResponse> manual = service.ExecuteAsync(1, ReadRequest());
        releaseFirst.TrySetResult();
        await allCompleted.Task.WaitAsync(TestTimeout);
        await manual.WaitAsync(TestTimeout);

        Assert.Equal("manual", completionOrder.ElementAt(1));
        Assert.Equal(3, service.GetStatistics().CompletedCommands);
        await service.StopAsync();
    }

    [Fact]
    public async Task TimeoutRetriesOnFreshConnectionAndRecordsMetrics()
    {
        FakeModbusTransport transport = new();
        int sendCount = 0;
        transport.SendHandler = (frame, _) =>
        {
            if (Interlocked.Increment(ref sendCount) == 2)
            {
                transport.RespondWithRegisters(frame, 0x55AA);
            }

            return ValueTask.CompletedTask;
        };
        MasterServiceOptions options = FastOptions(retryCount: 1) with
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(20),
            ReconnectDelay = TimeSpan.FromMilliseconds(1)
        };
        await using MasterService service = new(
            transport,
            new TcpMasterFrameCodec(),
            transport.OpenAsync,
            options);
        await service.StartAsync();

        ModbusResponse response = await service.ExecuteAsync(1, ReadRequest()).WaitAsync(TestTimeout);
        MasterStatisticsSnapshot statistics = service.GetStatistics();

        Assert.Equal(0x55AA, Assert.Single(response.GetRegisters()));
        Assert.Equal(1, statistics.Timeouts);
        Assert.Equal(1, statistics.Retries);
        Assert.Equal(1, statistics.Reconnects);
        Assert.Equal(2, statistics.SentFrames);
        Assert.Equal(2, transport.OpenCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task PollingDecodesScaledPointAndCompressesUnchangedHistory()
    {
        FakeModbusTransport transport = new();
        transport.SendHandler = (frame, _) =>
        {
            transport.RespondWithRegisters(frame, 20);
            return ValueTask.CompletedTask;
        };
        TrendService trend = new();
        await using MasterService service = new(
            transport,
            new TcpMasterFrameCodec(),
            transport.OpenAsync,
            FastOptions(retryCount: 0),
            trend);
        MasterDataPoint point = new()
        {
            Name = "温度",
            DataType = ModbusDataType.UInt16,
            Scale = 0.5,
            Offset = 1,
            HistoryEnabled = true
        };
        PollSchedule schedule = CreatePollSchedule(point);
        service.ConfigurePolling([schedule]);
        TaskCompletionSource twoResponses = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int responseCount = 0;
        MasterPointValue? lastValue = null;
        service.ResponseReceived += (_, args) =>
        {
            lastValue = Assert.Single(args.PointValues);
            if (Interlocked.Increment(ref responseCount) == 2)
            {
                twoResponses.TrySetResult();
            }
        };
        await service.StartAsync();
        await twoResponses.Task.WaitAsync(TestTimeout);
        await service.StopAsync();

        Assert.Equal(20, Assert.IsType<ushort>(lastValue!.RawValue));
        Assert.Equal(11, lastValue.ScaledValue);
        Assert.Single(trend.GetSnapshot(point.Id));
        Assert.True(service.GetStatistics().AverageResponseMilliseconds >= 0);
    }

    [Fact]
    public async Task StopCancelsPendingCommand()
    {
        FakeModbusTransport transport = new();
        await using MasterService service = new(
            transport,
            new TcpMasterFrameCodec(),
            transport.OpenAsync,
            FastOptions(retryCount: 0) with { ResponseTimeout = TimeSpan.FromSeconds(10) });
        await service.StartAsync();
        Task<ModbusResponse> pending = service.ExecuteAsync(1, ReadRequest());
        await transport.SentFrames.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        await service.StopAsync().AsTask().WaitAsync(TestTimeout);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(service.IsRunning);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public void FrameCodecsIgnoreMismatchedStationAndTransaction()
    {
        ModbusRequest request = ReadRequest();
        ModbusResponse response = ModbusResponse.CreateReadRegistersResponse(request, [1]);
        byte[] tcp = ModbusTcpFrame.FromResponse(10, 1, response).ToArray();
        TcpMasterFrameCodec tcpCodec = new();
        Assert.Null(tcpCodec.Decode(tcp, 1, 11, request));
        Assert.NotNull(tcpCodec.Decode(tcp, 1, 10, request));

        byte[] rtu = ModbusRtuFrame.FromResponse(1, response).ToArray();
        RtuMasterFrameCodec rtuCodec = new();
        Assert.Null(rtuCodec.Decode(rtu, 2, 0, request));
        Assert.NotNull(rtuCodec.Decode(rtu, 1, 0, request));
    }

    private static MasterServiceOptions FastOptions(int retryCount)
    {
        return new MasterServiceOptions
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(100),
            RetryCount = retryCount,
            RetryDelay = TimeSpan.FromMilliseconds(1),
            ReconnectDelay = TimeSpan.Zero,
            FrameInterval = TimeSpan.Zero,
            QueueCapacity = 16
        };
    }

    private static ModbusRequest ReadRequest()
    {
        return ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1);
    }

    private static PollSchedule CreatePollSchedule(MasterDataPoint? point = null)
    {
        MasterReadBlock block = new()
        {
            Function = ModbusFunction.ReadHoldingRegisters,
            StartAddress = 0,
            Quantity = 1,
            Points = point is null ? [] : [point]
        };
        return new PollSchedule
        {
            DeviceId = Guid.NewGuid(),
            BlockId = block.Id,
            UnitId = 1,
            Request = ReadRequest(),
            Interval = TimeSpan.FromMilliseconds(10),
            Block = block,
            ResponseTimeout = TimeSpan.FromMilliseconds(100),
            RetryCount = 0
        };
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TestTimeout);
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }
}
