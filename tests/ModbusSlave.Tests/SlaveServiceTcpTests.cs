// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ModbusCore.ModbusProtocol;

namespace ModbusSlave.Tests;

public sealed class SlaveServiceTcpTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TcpServerHandlesMultipleStationsOnIndependentClients()
    {
        SlaveStationManager stations = new([1, 2]);
        stations.GetOrAddStation(1).WriteHoldingRegisters(0, [0x1111]);
        stations.GetOrAddStation(2).WriteHoldingRegisters(0, [0x2222]);
        await using SlaveService service = new(stations);
        using CancellationTokenSource timeout = new(TestTimeout);
        await service.StartTcpAsync(IPAddress.Loopback, 0, timeout.Token);
        using TcpClient firstClient = new();
        using TcpClient secondClient = new();
        await firstClient.ConnectAsync(IPAddress.Loopback, service.TcpEndPoint!.Port, timeout.Token);
        await secondClient.ConnectAsync(IPAddress.Loopback, service.TcpEndPoint!.Port, timeout.Token);

        Task<ModbusTcpFrame> firstResponse = ExchangeAsync(
            firstClient,
            ModbusTcpFrame.FromRequest(
                1,
                1,
                ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1)).ToArray(),
            timeout.Token);
        Task<ModbusTcpFrame> secondResponse = ExchangeAsync(
            secondClient,
            ModbusTcpFrame.FromRequest(
                2,
                2,
                ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1)).ToArray(),
            timeout.Token);
        await Task.WhenAll(firstResponse, secondResponse);

        ModbusRequest request = ModbusRequest.CreateRead(ModbusFunction.ReadHoldingRegisters, 0, 1);
        Assert.Equal(
            [0x1111],
            ModbusResponse.Parse(firstResponse.Result.Pdu.Span, request).GetRegisters());
        Assert.Equal(
            [0x2222],
            ModbusResponse.Parse(secondResponse.Result.Pdu.Span, request).GetRegisters());
        Assert.Equal(2, service.TcpConnections.Count);
    }

    [Fact]
    public async Task TcpServerReturnsIllegalFunctionForUnknownFunctionCode()
    {
        await using SlaveService service = new(new SlaveStationManager([1]));
        using CancellationTokenSource timeout = new(TestTimeout);
        await service.StartTcpAsync(IPAddress.Loopback, 0, timeout.Token);
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, service.TcpEndPoint!.Port, timeout.Token);
        byte[] request = new ModbusTcpFrame(0x1234, 1, [0x07]).ToArray();

        ModbusTcpFrame response = await ExchangeAsync(client, request, timeout.Token);

        Assert.Equal(0x1234, response.TransactionId);
        Assert.Equal(1, response.UnitId);
        Assert.Equal([0x87, 0x01], response.Pdu.ToArray());
    }

    private static async Task<ModbusTcpFrame> ExchangeAsync(
        TcpClient client,
        byte[] request,
        CancellationToken cancellationToken)
    {
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(request, cancellationToken);
        byte[] header = new byte[6];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int remaining = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        byte[] frame = new byte[6 + remaining];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(6), cancellationToken);
        return ModbusTcpFrame.Parse(frame);
    }
}
