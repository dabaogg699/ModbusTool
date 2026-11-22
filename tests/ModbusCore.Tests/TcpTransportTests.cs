// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ModbusCore.ModbusProtocol;
using ModbusCore.Transport;

namespace ModbusCore.Tests;

public sealed class TcpTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ClientReceivesFragmentedAndCoalescedFramesAndReportsDisconnect()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
            await using TcpTransport transport = new();
            Channel<byte[]> received = Channel.CreateUnbounded<byte[]>();
            TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.FrameReceived += (_, args) => received.Writer.TryWrite(args.Data.ToArray());
            transport.Disconnected += (_, _) => disconnected.TrySetResult();

            using CancellationTokenSource timeout = new(TestTimeout);
            await transport.ConnectAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)listener.LocalEndpoint).Port,
                timeout.Token);
            using TcpClient serverClient = await acceptTask.WaitAsync(timeout.Token);
            NetworkStream stream = serverClient.GetStream();
            ModbusTcpFrame first = TransportParserTests.CreateRequestFrame(10, 1);
            ModbusTcpFrame second = TransportParserTests.CreateRequestFrame(11, 2);
            byte[] firstBytes = first.ToArray();

            await stream.WriteAsync(firstBytes.AsMemory(0, 3), timeout.Token);
            byte[] remainingAndSecond = [.. firstBytes.AsSpan(3), .. second.ToArray()];
            await stream.WriteAsync(remainingAndSecond, timeout.Token);

            Assert.Equal(firstBytes, await received.Reader.ReadAsync(timeout.Token));
            Assert.Equal(second.ToArray(), await received.Reader.ReadAsync(timeout.Token));
            Assert.True(transport.IsOpen);

            serverClient.Close();
            await disconnected.Task.WaitAsync(timeout.Token);
            Assert.False(transport.IsOpen);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ClientSendsCompleteFrameWithCancellationSupport()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
            await using TcpTransport transport = new();
            TaskCompletionSource<byte[]> sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.FrameSent += (_, args) => sent.TrySetResult(args.Data.ToArray());
            using CancellationTokenSource timeout = new(TestTimeout);
            await transport.ConnectAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)listener.LocalEndpoint).Port,
                timeout.Token);
            using TcpClient serverClient = await acceptTask.WaitAsync(timeout.Token);
            byte[] frame = TransportParserTests.CreateRequestFrame(0x1234, 1).ToArray();

            await transport.SendAsync(frame, timeout.Token);
            byte[] received = new byte[frame.Length];
            await serverClient.GetStream().ReadExactlyAsync(received, timeout.Token);

            Assert.Equal(frame, received);
            Assert.Equal(frame, await sent.Task.WaitAsync(timeout.Token));
            await transport.CloseAsync();
            Assert.False(transport.IsOpen);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CancelledConnectDoesNotLeaveClientOpen()
    {
        await using TcpTransport transport = new();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.ConnectAsync(IPAddress.Loopback.ToString(), 502, cancelled.Token));
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task ServerRoutesFramesToIndependentClientConnections()
    {
        await using TcpServerTransport server = new();
        Channel<TransportDataEventArgs> received = Channel.CreateUnbounded<TransportDataEventArgs>();
        TaskCompletionSource bothConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.FrameReceived += (_, args) => received.Writer.TryWrite(args);
        server.ClientConnected += (_, _) =>
        {
            if (server.Connections.Count == 2)
            {
                bothConnected.TrySetResult();
            }
        };

        using CancellationTokenSource timeout = new(TestTimeout);
        await server.StartAsync(IPAddress.Loopback, 0, timeout.Token);
        int port = server.LocalEndPoint!.Port;
        using TcpClient firstClient = new();
        using TcpClient secondClient = new();
        await firstClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await secondClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await bothConnected.Task.WaitAsync(timeout.Token);

        byte[] request = TransportParserTests.CreateRequestFrame(21, 3).ToArray();
        await secondClient.GetStream().WriteAsync(request, timeout.Token);
        TransportDataEventArgs args = await received.Reader.ReadAsync(timeout.Token);

        Assert.Equal(request, args.Data.ToArray());
        Assert.Contains(server.Connections, connection => connection.ConnectionId == args.ConnectionId);
        ModbusRequest parsedRequest = ModbusRequest.Parse(ModbusTcpFrame.Parse(request).Pdu.Span);
        ModbusResponse response = ModbusResponse.CreateReadRegistersResponse(parsedRequest, [0x55AA]);
        byte[] responseFrame = ModbusTcpFrame.FromResponse(21, 3, response).ToArray();
        await server.SendAsync(args.ConnectionId!, responseFrame, timeout.Token);

        byte[] actualResponse = new byte[responseFrame.Length];
        await secondClient.GetStream().ReadExactlyAsync(actualResponse, timeout.Token);
        Assert.Equal(responseFrame, actualResponse);

        await server.StopAsync();
        Assert.False(server.IsListening);
        Assert.Empty(server.Connections);
    }
}
