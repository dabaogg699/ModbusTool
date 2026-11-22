// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Net;
using ModbusCore.Transport;
using ModbusSlave;

namespace ModbusMaster.Tests;

/// <summary>
/// G15 专项测试：模板加载后主站可直接轮询、从站默认监听地址基线。
/// </summary>
public class G15DefaultTemplateIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task LoadedDefaultTemplate_PollsLocalSlaveDirectly()
    {
        // 模拟从站在 127.0.0.1 随机端口监听，保持寄存器 0-9 写入固定值。
        SlaveStationManager stations = new([1]);
        stations.GetOrAddStation(1).WriteHoldingRegisters(
            0,
            [0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x0006, 0x0007, 0x0008, 0x0009, 0x000A]);
        await using SlaveService slave = new(stations);
        using CancellationTokenSource timeout = new(TestTimeout);
        await slave.StartTcpAsync(IPAddress.Loopback, 0, timeout.Token);

        // 加载内置默认模板（与启动流程 DefaultTemplateService.LoadBuiltInAsync 一致），
        // 用模板构建轮询计划，直接启动主站——不重新配置任何点位。
        ModbusMaster.MasterWorkspace workspace =
            await ModbusTool.App.Services.DefaultTemplateService.LoadBuiltInAsync();
        MasterWorkspaceManager manager = new();
        manager.LoadModel(workspace);
        IReadOnlyList<PollSchedule> schedules = manager.BuildPollSchedules();

        Assert.NotEmpty(schedules);
        PollSchedule schedule = schedules[0];
        Assert.Equal((byte)1, schedule.UnitId);
        Assert.Equal(ModbusCore.ModbusProtocol.ModbusFunction.ReadHoldingRegisters, schedule.Request.Function);
        Assert.Equal(0, schedule.Request.Address);
        Assert.Equal((ushort)10, schedule.Request.Quantity);

        TcpTransport transport = new();
        await using MasterService master = new(
            transport,
            new TcpMasterFrameCodec(),
            cancellationToken => transport.ConnectAsync(IPAddress.Loopback.ToString(), slave.TcpEndPoint!.Port, cancellationToken),
            new MasterServiceOptions
            {
                ResponseTimeout = TimeSpan.FromSeconds(1),
                RetryCount = 0,
                RetryDelay = TimeSpan.Zero,
                ReconnectDelay = TimeSpan.Zero,
                FrameInterval = TimeSpan.Zero,
                QueueCapacity = 100
            });
        ModbusCore.ModbusProtocol.ModbusResponse? lastResponse = null;
        master.ResponseReceived += (_, args) => lastResponse = args.Response;
        master.ConfigurePolling(schedules);
        await master.StartAsync(timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
        await master.StopAsync().AsTask().WaitAsync(timeout.Token);

        MasterStatisticsSnapshot statistics = master.GetStatistics();
        Assert.True(statistics.SuccessfulExchanges >= 1, $"仅完成 {statistics.SuccessfulExchanges} 次轮询。");
        Assert.Equal(0, statistics.Errors);
        Assert.Equal(0, statistics.Timeouts);
        Assert.NotNull(lastResponse);
        Assert.Equal(
            new ushort[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 0x000A },
            lastResponse!.GetRegisters());
    }

    [Fact]
    public void SlaveConfiguration_DefaultTcpAddressIsLoopback()
    {
        SlaveConfiguration configuration = new();
        Assert.Equal("127.0.0.1", configuration.TcpAddress);
        Assert.Equal(502, configuration.TcpPort);
    }
}
