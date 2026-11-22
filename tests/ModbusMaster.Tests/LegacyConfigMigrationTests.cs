// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Text.Json;
using ModbusCore.ByteConvert;

namespace ModbusMaster.Tests;

public sealed class LegacyConfigMigrationTests
{
    [Fact]
    public async Task RealLegacyConfigMigratesAndNeverOverwritesSource()
    {
        string sourcePath = GetRepositoryFile("tests", "fixtures", "legacy", "MBScan", "config.txt");
        string originalText = await File.ReadAllTextAsync(sourcePath);
        string outputPath = Path.Combine(Path.GetTempPath(), $"legacy-master-{Guid.NewGuid():N}.json");
        try
        {
            MasterWorkspaceManager manager = new();
            await manager.LoadAsync(sourcePath);

            Assert.Null(manager.FilePath);
            Assert.True(manager.IsDirty);
            Assert.Equal(3, manager.ImportedWorkspaces.Count);
            MasterWorkspace current = manager.Current;
            Assert.Equal(MasterConnectionKind.Tcp, current.Connection.Kind);
            Assert.Equal("127.0.0.1", current.Connection.Host);
            Assert.Equal(502, current.Connection.Port);
            Assert.Equal(500, current.Connection.ResponseTimeoutMs);
            Assert.Equal(1, current.Connection.RetryCount);
            Assert.Equal(50, current.Connection.FrameIntervalMs);
            MasterDevice device = Assert.Single(current.Devices);
            Assert.Equal(300, device.PollIntervalMs);
            MasterReadBlock block = Assert.Single(device.ReadBlocks);
            Assert.Equal(30, block.Quantity);
            Assert.Equal(ModbusDataType.Float, Assert.Single(block.Points).DataType);
            Assert.Equal([234, 234, 1278, 799], manager.ImportedLegacyConfiguration!.WindowRectangle);

            manager.ActivateImportedWorkspace(manager.ImportedWorkspaces[2]);
            Assert.Equal("test", manager.Current.Name);
            MasterReadBlock testBlock = Assert.Single(Assert.Single(manager.Current.Devices).ReadBlocks);
            Assert.Equal(ModbusDataType.UInt16, testBlock.Points[0].DataType);
            MasterDataPoint history = testBlock.Points[1];
            Assert.Equal(ModbusDataType.Int16, history.DataType);
            Assert.True(history.HistoryEnabled);

            await manager.SaveAsync(outputPath);
            Assert.Equal(originalText, await File.ReadAllTextAsync(sourcePath));
            using JsonDocument converted = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(1, converted.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.False(converted.RootElement.TryGetProperty("workspaces", out _));

            MasterWorkspaceManager reloaded = new();
            await reloaded.LoadAsync(outputPath);
            Assert.Null(reloaded.ImportedLegacyConfiguration);
            Assert.Equal("test", reloaded.Current.Name);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void RegisterAndHistoryTypeIdsUseSeparateMappings()
    {
        const string json = """
        {
          "commhistory": ["COM6,19200,E,8,1"],
          "workspaces": [[
            "映射",
            ["2,100,4,5,3"],
            ["2,100,5,2,历史双精度"]
          ]]
        }
        """;
        using JsonDocument document = JsonDocument.Parse(json);
        LegacyMasterConfiguration legacy = LegacyMasterConfigMigrator.Migrate(document.RootElement);
        MasterWorkspace workspace = Assert.Single(legacy.Workspaces);
        Assert.Equal(MasterConnectionKind.SerialRtu, workspace.Connection.Kind);
        Assert.Equal("COM6", workspace.Connection.PortName);
        Assert.Equal(19200, workspace.Connection.BaudRate);
        Assert.Equal("Even", workspace.Connection.Parity);
        MasterReadBlock block = Assert.Single(Assert.Single(workspace.Devices).ReadBlocks);
        Assert.Equal(ModbusDataType.UInt32, block.Points[0].DataType);
        Assert.Equal(ModbusByteOrder.Order2301, block.Points[0].ByteOrder);
        Assert.Equal(ModbusDataType.Double, block.Points[1].DataType);
        Assert.Equal(ModbusByteOrder.Order0123, block.Points[1].ByteOrder);
        Assert.Equal("历史双精度", block.Points[1].Name);
    }

    [Fact]
    public void InvalidLegacyRecordFailsWithContext()
    {
        const string json = """
        { "workspaces": [["bad", ["1,0,10"], []]] }
        """;
        using JsonDocument document = JsonDocument.Parse(json);
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => LegacyMasterConfigMigrator.Migrate(document.RootElement));
        Assert.Contains("普通点位 1", exception.Message);
        Assert.Contains("缺少字段", exception.Message);
    }

    private static string GetRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = parts.Aggregate(directory.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("找不到原配置样例。", Path.Combine(parts));
    }
}
