// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ByteConvert;
using ModbusCore.ModbusProtocol;

namespace ModbusMaster.Tests;

public sealed class WorkspaceAndTrendTests
{
    [Fact]
    public async Task WorkspaceRoundTripsAndBuildsEnabledPollSchedules()
    {
        MasterDataPoint point = new()
        {
            Name = "压力",
            AddressOffset = 0,
            DataType = ModbusDataType.Float,
            ByteOrder = ModbusByteOrder.Order3210,
            HistoryEnabled = true
        };
        MasterReadBlock block = new()
        {
            Name = "工艺寄存器",
            Function = ModbusFunction.ReadHoldingRegisters,
            StartAddress = 100,
            Quantity = 2,
            Points = [point]
        };
        MasterDevice enabledDevice = new()
        {
            Name = "一号站",
            UnitId = 3,
            PollIntervalMs = 250,
            ReadBlocks = [block]
        };
        MasterDevice disabledDevice = new()
        {
            Name = "停用站",
            Enabled = false,
            ReadBlocks = [new MasterReadBlock()]
        };
        MasterWorkspaceManager manager = new();
        manager.New("测试工作区");
        manager.Current.Devices.AddRange([enabledDevice, disabledDevice]);
        string filePath = Path.Combine(Path.GetTempPath(), $"modbus-workspace-{Guid.NewGuid():N}.json");
        try
        {
            await manager.SaveAsync(filePath);
            Assert.False(manager.IsDirty);

            MasterWorkspaceManager loaded = new();
            await loaded.LoadAsync(filePath);
            PollSchedule schedule = Assert.Single(loaded.BuildPollSchedules());

            Assert.Equal("测试工作区", loaded.Current.Name);
            Assert.Equal(1, loaded.Current.SchemaVersion);
            Assert.Equal(enabledDevice.Id, schedule.DeviceId);
            Assert.Equal(block.Id, schedule.BlockId);
            Assert.Equal(TimeSpan.FromMilliseconds(250), schedule.Interval);
            Assert.Equal(ModbusFunction.ReadHoldingRegisters, schedule.Request.Function);
            Assert.Equal(100, schedule.Request.Address);
            Assert.Equal(2, schedule.Request.Quantity);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public void WorkspaceRejectsPointOutsideReadBlock()
    {
        MasterWorkspaceManager manager = new();
        MasterReadBlock block = new()
        {
            Quantity = 1,
            Points = [new MasterDataPoint { DataType = ModbusDataType.Double }]
        };
        manager.Current.Devices.Add(new MasterDevice { ReadBlocks = [block] });

        Assert.Throws<InvalidDataException>(() => manager.BuildPollSchedules());
    }

    [Fact]
    public void TrendCompressesRepeatedValuesAndPreservesBreakBoundaries()
    {
        TrendService trend = new();
        Guid pointId = Guid.NewGuid();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        trend.Record(pointId, 5, timestamp);
        trend.Record(pointId, 5, timestamp.AddSeconds(1));
        trend.MarkBreak(pointId, timestamp.AddSeconds(2));
        trend.MarkBreak(pointId, timestamp.AddSeconds(3));
        trend.Record(pointId, 5, timestamp.AddSeconds(4));

        IReadOnlyList<TrendSample> samples = trend.GetSnapshot(pointId);
        Assert.Equal(3, samples.Count);
        Assert.Equal(5, samples[0].Value);
        Assert.True(samples[1].IsBreak);
        Assert.Null(samples[1].Value);
        Assert.Equal(5, samples[2].Value);
        Assert.False(samples[2].IsBreak);
    }

    [Fact]
    public void TrendEmitsHeartbeatForUnchangedValues()
    {
        TrendService trend = new();
        Guid pointId = Guid.NewGuid();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        trend.Record(pointId, 5, timestamp);
        trend.Record(pointId, 5, timestamp.AddSeconds(1));
        trend.Record(pointId, 5, timestamp.AddSeconds(3));

        IReadOnlyList<TrendSample> samples = trend.GetSnapshot(pointId);
        Assert.Equal(2, samples.Count);
        Assert.Equal(timestamp, samples[0].Timestamp);
        Assert.Equal(timestamp.AddSeconds(3), samples[1].Timestamp);
        Assert.All(samples, sample => Assert.Equal(5, sample.Value));
    }

    [Fact]
    public void TrendSeriesHonorsCapacity()
    {
        TrendService trend = new(seriesCapacity: 2);
        Guid pointId = Guid.NewGuid();
        trend.Record(pointId, 1);
        trend.Record(pointId, 2);
        trend.Record(pointId, 3);

        Assert.Equal([2d, 3d], trend.GetSnapshot(pointId).Select(sample => sample.Value!.Value).ToArray());

        TrendService defaultTrend = new();
        for (int value = 0; value < 2005; value++)
        {
            defaultTrend.Record(pointId, value);
        }

        IReadOnlyList<TrendSample> defaultSamples = defaultTrend.GetSnapshot(pointId);
        Assert.Equal(2000, defaultSamples.Count);
        Assert.Equal(5, defaultSamples[0].Value);
        Assert.Equal(2004, defaultSamples[^1].Value);
    }

    [Fact]
    public async Task PointGroupsNotesAndDevicePollOrderRoundTrip()
    {
        MasterWorkspaceManager manager = new();
        manager.New("分组与轮询顺序");
        manager.Current.PointGroups.Add(new MasterPointGroup { Name = "工艺", IsExpanded = false });
        MasterDevice first = new()
        {
            Name = "先轮询",
            UnitId = 1,
            PollIntervalMs = 100,
            PollOffsetMs = 20,
            ReadBlocks =
            [
                new MasterReadBlock
                {
                    Name = "块1",
                    Function = ModbusFunction.ReadHoldingRegisters,
                    StartAddress = 0,
                    Quantity = 10,
                    Points =
                    [
                        new MasterDataPoint
                        {
                            Name = "点位A",
                            AddressOffset = 0,
                            GroupName = "工艺",
                            Note = "一号点备注"
                        }
                    ]
                }
            ]
        };
        MasterDevice second = new()
        {
            Name = "后轮询",
            UnitId = 2,
            PollIntervalMs = 250,
            ReadBlocks = [new MasterReadBlock { Name = "块2", Function = ModbusFunction.ReadInputRegisters, Quantity = 4 }]
        };
        manager.Current.Devices.AddRange([first, second]);
        string filePath = Path.Combine(Path.GetTempPath(), $"modbus-groups-{Guid.NewGuid():N}.json");
        try
        {
            await manager.SaveAsync(filePath);
            MasterWorkspaceManager loaded = new();
            await loaded.LoadAsync(filePath);

            MasterPointGroup group = Assert.Single(loaded.Current.PointGroups);
            Assert.Equal("工艺", group.Name);
            Assert.False(group.IsExpanded);
            MasterDataPoint point = loaded.Current.Devices[0].ReadBlocks[0].Points[0];
            Assert.Equal("工艺", point.GroupName);
            Assert.Equal("一号点备注", point.Note);

            PollSchedule[] schedules = loaded.BuildPollSchedules().ToArray();
            Assert.Equal(2, schedules.Length);
            Assert.Equal(first.Id, schedules[0].DeviceId);
            Assert.Equal(second.Id, schedules[1].DeviceId);
            Assert.Equal(TimeSpan.FromMilliseconds(20), schedules[0].StartDelay);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
