// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Net;
using System.Net.Sockets;
using ModbusCore.ByteConvert;
using ModbusCore.ModbusDataModel;

namespace ModbusSlave.Tests;

public sealed class SimulationServiceTests
{
    [Fact]
    public async Task IncrementUsesExistingByteOrderAndWrapsAtConfiguredRange()
    {
        using SlaveStationManager stations = new([1]);
        await using SlaveService slave = new(stations);
        await using SimulationService simulation = new(stations, slave);
        DataSimulationSettings settings = new()
        {
            RegisterCount = 2,
            DataType = ModbusDataType.UInt32,
            ByteOrder = ModbusByteOrder.Order3210,
            Minimum = 0,
            Maximum = 2,
            Step = 1
        };

        simulation.SimulateDataOnce(settings);
        simulation.SimulateDataOnce(settings);
        simulation.SimulateDataOnce(settings);

        ushort[] registers = stations.GetOrAddStation(1).ReadHoldingRegisters(0, 2);
        Assert.Equal(0u, RegisterConverter.Decode(registers, settings.DataType, settings.ByteOrder));
    }

    [Fact]
    public async Task RandomSimulationUpdatesOnlySelectedRegisterAreaAndStaysInRange()
    {
        using SlaveStationManager stations = new([1]);
        await using SlaveService slave = new(stations);
        await using SimulationService simulation = new(stations, slave);
        DataSimulationSettings settings = new()
        {
            Area = ModbusDataArea.InputRegisters,
            RegisterCount = 4,
            DataType = ModbusDataType.Int16,
            Mode = DataSimulationMode.Random,
            Minimum = -3,
            Maximum = 4
        };

        simulation.SimulateDataOnce(settings);

        short[] values = stations.GetOrAddStation(1).ReadInputRegisters(0, 4)
            .Select(value => (short)value).ToArray();
        Assert.All(values, value => Assert.InRange(value, (short)-3, (short)4));
        Assert.Equal([0, 0, 0, 0], stations.GetOrAddStation(1).ReadHoldingRegisters(0, 4));
    }

    [Fact]
    public async Task RawFramesAreSentToEveryConnectedTcpClient()
    {
        await using SlaveService slave = new(new SlaveStationManager([1]));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await slave.StartTcpAsync(IPAddress.Loopback, 0, timeout.Token);
        using TcpClient first = new();
        using TcpClient second = new();
        await first.ConnectAsync(IPAddress.Loopback, slave.TcpEndPoint!.Port, timeout.Token);
        await second.ConnectAsync(IPAddress.Loopback, slave.TcpEndPoint!.Port, timeout.Token);
        byte[] frame = [0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A];

        int sentCount = await slave.SendRawAsync(frame, cancellationToken: timeout.Token);
        byte[] firstReceived = new byte[frame.Length];
        byte[] secondReceived = new byte[frame.Length];
        await first.GetStream().ReadExactlyAsync(firstReceived, timeout.Token);
        await second.GetStream().ReadExactlyAsync(secondReceived, timeout.Token);

        Assert.Equal(2, sentCount);
        Assert.Equal(frame, firstReceived);
        Assert.Equal(frame, secondReceived);
    }
}
