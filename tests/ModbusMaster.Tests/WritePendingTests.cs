// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusProtocol;

namespace ModbusMaster.Tests;

public sealed class WritePendingTests
{
    [Fact]
    public void ConcurrentPollingCannotOverwritePreviewUntilWriteResponseArrives()
    {
        MasterWritePendingTracker tracker = new();
        ModbusRequest write = ModbusRequest.CreateWriteSingleRegister(10, 4096);
        string displayedValue = write.Value.ToString();
        Assert.True(tracker.TryBegin(1, write.Address));

        Parallel.For(0, 1000, index =>
        {
            // 与 MasterViewModel.UpdateRegisterRows 相同：锁定期间丢弃轮询显示刷新。
            if (!tracker.IsPending(1, write.Address))
            {
                displayedValue = index.ToString();
            }
        });

        Assert.Equal("4096", displayedValue);
        Assert.True(tracker.IsPending(1, write.Address));

        ModbusResponse response = ModbusResponse.CreateWriteResponse(write);
        Assert.True(tracker.Complete(1, response.Address));
        displayedValue = response.Value.ToString();
        Assert.Equal("4096", displayedValue);

        if (!tracker.IsPending(1, write.Address))
        {
            displayedValue = "8192";
        }

        Assert.Equal("8192", displayedValue);
    }

    [Fact]
    public void PendingWriteRejectsDuplicateAndIsScopedByStationAndAddress()
    {
        MasterWritePendingTracker tracker = new();
        Assert.True(tracker.TryBegin(1, 10));
        Assert.False(tracker.TryBegin(1, 10));
        Assert.True(tracker.TryBegin(2, 10));
        Assert.True(tracker.TryBegin(1, 11));
        Assert.Equal(3, tracker.Count);

        Assert.True(tracker.Complete(1, 10));
        Assert.False(tracker.IsPending(1, 10));
        Assert.True(tracker.IsPending(2, 10));
        Assert.True(tracker.IsPending(1, 11));
    }

    [Fact]
    public void FailedWriteCanReleaseLockForNextAttempt()
    {
        MasterWritePendingTracker tracker = new();
        Assert.True(tracker.TryBegin(1, 20));

        // 超时、断连和异常应答都通过 Complete 解除锁定。
        Assert.True(tracker.Complete(1, 20));
        Assert.False(tracker.IsPending(1, 20));
        Assert.True(tracker.TryBegin(1, 20));
    }
}
