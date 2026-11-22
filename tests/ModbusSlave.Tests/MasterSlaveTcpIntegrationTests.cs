// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;
using ModbusMaster;
using Xunit.Abstractions;

namespace ModbusSlave.Tests;

public sealed class MasterSlaveTcpIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private readonly ITestOutputHelper _output;

    public MasterSlaveTcpIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task RealTcpSupportsAllEightFunctionCodesAndIndependentDataAreas()
    {
        SlaveStationManager stations = new([1]);
        stations.GetOrAddStation(1).WriteDiscreteInputs(8, [true, false, true, true, false, false, true, false, true]);
        stations.GetOrAddStation(1).WriteInputRegisters(40, [0x1111, 0x2222, 0x3333]);
        await using SlaveService slave = new(stations);
        using CancellationTokenSource timeout = new(TestTimeout);
        await slave.StartTcpAsync(IPAddress.Loopback, 0, timeout.Token);
        await using MasterService master = CreateMaster(slave.TcpEndPoint!.Port);
        await master.StartAsync(timeout.Token);

        bool[] coils = [true, false, true, true, false, true, false, false, true, true, false, true];
        ModbusResponse writeCoils = await ExecuteAsync(
            master,
            ModbusRequest.CreateWriteMultipleCoils(8, coils),
            timeout.Token);
        Assert.Equal(ModbusFunction.WriteMultipleCoils, writeCoils.Function);
        Assert.Equal(coils, await ReadBitsAsync(master, ModbusFunction.ReadCoils, 8, (ushort)coils.Length, timeout.Token));
        Assert.Equal(
            [true, false, true, true, false, false, true, false, true],
            await ReadBitsAsync(master, ModbusFunction.ReadDiscreteInputs, 8, 9, timeout.Token));

        ModbusResponse writeSingleCoil = await ExecuteAsync(
            master,
            ModbusRequest.CreateWriteSingleCoil(30, true),
            timeout.Token);
        Assert.Equal(0xFF00, writeSingleCoil.Value);
        Assert.Equal([true], await ReadBitsAsync(master, ModbusFunction.ReadCoils, 30, 1, timeout.Token));

        await ExecuteAsync(master, ModbusRequest.CreateWriteSingleRegister(30, 0xBEEF), timeout.Token);
        Assert.Equal(
            [0xBEEF],
            await ReadRegistersAsync(master, ModbusFunction.ReadHoldingRegisters, 30, 1, timeout.Token));

        ushort[] registers = [0x1001, 0x1002, 0x1003, 0x1004, 0x1005];
        await ExecuteAsync(master, ModbusRequest.CreateWriteMultipleRegisters(40, registers), timeout.Token);
        Assert.Equal(
            registers,
            await ReadRegistersAsync(master, ModbusFunction.ReadHoldingRegisters, 40, (ushort)registers.Length, timeout.Token));
        Assert.Equal(
            [0x1111, 0x2222, 0x3333],
            await ReadRegistersAsync(master, ModbusFunction.ReadInputRegisters, 40, 3, timeout.Token));

        MasterStatisticsSnapshot statistics = master.GetStatistics();
        Assert.Equal(10, statistics.SuccessfulExchanges);
        Assert.Equal(0, statistics.Errors);
        Assert.Equal(0, statistics.Timeouts);
        await master.StopAsync();
    }

    [Fact]
    public async Task RealTcpPollsTenStationsOf125RegistersWithoutErrorsOrQueueGrowth()
    {
        const int stationCount = 10;
        const int registerCount = 125;
        Dictionary<byte, ushort[]> expected = Enumerable.Range(1, stationCount)
            .ToDictionary(
                unitId => (byte)unitId,
                unitId => Enumerable.Range(0, registerCount)
                    .Select(index => (ushort)(unitId * 1000 + index))
                    .ToArray());
        SlaveStationManager stations = new(expected.Keys);
        foreach ((byte unitId, ushort[] registers) in expected)
        {
            stations.GetOrAddStation(unitId).WriteHoldingRegisters(0, registers);
        }

        await using SlaveService slave = new(stations);
        using CancellationTokenSource timeout = new(TestTimeout);
        await slave.StartTcpAsync(IPAddress.Loopback, 0, timeout.Token);
        await using MasterService master = CreateMaster(slave.TcpEndPoint!.Port);
        ConcurrentDictionary<byte, int> responsesByStation = new();
        ConcurrentQueue<Exception> validationErrors = new();
        master.ResponseReceived += (_, args) =>
        {
            try
            {
                Assert.Equal(expected[args.UnitId], args.Response.GetRegisters());
                responsesByStation.AddOrUpdate(args.UnitId, 1, (_, count) => count + 1);
            }
            catch (Exception exception)
            {
                validationErrors.Enqueue(exception);
            }
        };
        master.ConfigurePolling(expected.Keys.Select(unitId => new PollSchedule
        {
            DeviceId = Guid.NewGuid(),
            BlockId = Guid.NewGuid(),
            UnitId = unitId,
            Request = ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, registerCount),
            Interval = TimeSpan.FromMilliseconds(5),
            ResponseTimeout = TimeSpan.FromSeconds(1),
            RetryCount = 0
        }));

        long allocatedBefore = GC.GetTotalMemory(true);
        Stopwatch elapsed = Stopwatch.StartNew();
        await master.StartAsync(timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
        await master.StopAsync().AsTask().WaitAsync(timeout.Token);
        elapsed.Stop();
        long allocatedAfter = GC.GetTotalMemory(true);
        MasterStatisticsSnapshot statistics = master.GetStatistics();

        _output.WriteLine(
            $"10站×125寄存器: 用时={elapsed.Elapsed.TotalSeconds:F3}s, 成功={statistics.SuccessfulExchanges}, " +
            $"速率={statistics.SuccessfulExchanges / elapsed.Elapsed.TotalSeconds:F1}次/s, " +
            $"平均响应={statistics.AverageResponseMilliseconds:F3}ms, 超时={statistics.Timeouts}, " +
            $"错误={statistics.Errors}, 重试={statistics.Retries}, 丢弃={statistics.DroppedPollCommands}, " +
            $"剩余队列={master.QueuedCommandCount}, 托管内存={allocatedBefore}->{allocatedAfter}字节, " +
            $"各站={string.Join(',', responsesByStation.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}");

        Assert.Empty(validationErrors);
        Assert.Equal(stationCount, responsesByStation.Count);
        Assert.All(responsesByStation, pair => Assert.True(pair.Value >= 20, $"站号 {pair.Key} 仅完成 {pair.Value} 次轮询。"));
        Assert.True(statistics.SuccessfulExchanges >= 200, $"仅完成 {statistics.SuccessfulExchanges} 次轮询。");
        Assert.Equal(0, statistics.Timeouts);
        Assert.Equal(0, statistics.Errors);
        Assert.Equal(0, statistics.Retries);
        Assert.Equal(0, statistics.Reconnects);
        Assert.Equal(0, statistics.DroppedPollCommands);
        Assert.Equal(0, master.QueuedCommandCount);
    }

    private static MasterService CreateMaster(int port)
    {
        TcpTransport transport = new();
        return new MasterService(
            transport,
            new TcpMasterFrameCodec(),
            cancellationToken => transport.ConnectAsync(IPAddress.Loopback.ToString(), port, cancellationToken),
            new MasterServiceOptions
            {
                ResponseTimeout = TimeSpan.FromSeconds(1),
                RetryCount = 0,
                RetryDelay = TimeSpan.Zero,
                ReconnectDelay = TimeSpan.Zero,
                FrameInterval = TimeSpan.Zero,
                QueueCapacity = 1000
            });
    }

    private static async Task<ModbusResponse> ExecuteAsync(
        MasterService master,
        ModbusRequest request,
        CancellationToken cancellationToken)
    {
        ModbusResponse response = await master.ExecuteAsync(1, request, cancellationToken: cancellationToken)
            .WaitAsync(cancellationToken);
        Assert.False(response.IsException, response.ExceptionCode?.ToString());
        return response;
    }

    private static async Task<bool[]> ReadBitsAsync(
        MasterService master,
        ModbusFunction function,
        ushort address,
        ushort quantity,
        CancellationToken cancellationToken)
    {
        ModbusResponse response = await ExecuteAsync(
            master,
            ModbusRequest.CreateRead(function, address, quantity),
            cancellationToken);
        return response.GetBits();
    }

    private static async Task<ushort[]> ReadRegistersAsync(
        MasterService master,
        ModbusFunction function,
        ushort address,
        ushort quantity,
        CancellationToken cancellationToken)
    {
        ModbusResponse response = await ExecuteAsync(
            master,
            ModbusRequest.CreateRead(function, address, quantity),
            cancellationToken);
        return response.GetRegisters();
    }
}
