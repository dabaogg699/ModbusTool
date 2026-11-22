// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Text.Json;
using ModbusCore.ByteConvert;

namespace ModbusSlave.Tests;

public sealed class SlaveConfigurationTests
{
    [Fact]
    public async Task RealLegacyConfigMigratesAndRoundTripsVersionedConfiguration()
    {
        string sourcePath = GetRepositoryFile("tests", "fixtures", "legacy", "MBSlave", "config.txt");
        string originalText = await File.ReadAllTextAsync(sourcePath);
        string outputPath = Path.Combine(Path.GetTempPath(), $"legacy-slave-{Guid.NewGuid():N}.json");
        try
        {
            SlaveConfigurationManager manager = new();
            await manager.LoadAsync(sourcePath);

            Assert.True(manager.LegacyImported);
            Assert.Null(manager.FilePath);
            Assert.True(manager.IsDirty);
            Assert.Equal(10, manager.LegacyCommunicationHistory.Count);
            SlaveConfiguration configuration = manager.Current;
            SlaveStationConfiguration station = Assert.Single(configuration.Stations);
            Assert.Equal(1, station.UnitId);
            Assert.Equal(Path.Combine(Path.GetDirectoryName(sourcePath)!, "data.bin"), station.DataFilePath);
            Assert.False(configuration.UseTcp);
            Assert.True(configuration.UseSerial);
            Assert.Equal("COM1", configuration.PortName);
            Assert.Equal(19200, configuration.BaudRate);
            Assert.Equal("None", configuration.Parity);
            Assert.Equal("GB2312", configuration.SelectedEncoding);
            Assert.Equal(ModbusDataType.UInt16, configuration.TableDataType);
            Assert.True(configuration.TableDisplayHex);
            Assert.Equal(ModbusByteOrder.Order2301, configuration.TableByteOrder);

            await manager.SaveAsync(outputPath);
            Assert.Equal(originalText, await File.ReadAllTextAsync(sourcePath));
            using JsonDocument converted = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(1, converted.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(outputPath)!, Path.GetFileName(outputPath) + ".*.tmp"));

            SlaveConfigurationManager reloaded = new();
            await reloaded.LoadAsync(outputPath);
            Assert.False(reloaded.LegacyImported);
            Assert.Equal(outputPath, reloaded.FilePath);
            Assert.Equal(configuration.TableByteOrder, reloaded.Current.TableByteOrder);
            Assert.Equal(station.DataFilePath, Assert.Single(reloaded.Current.Stations).DataFilePath);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void LegacyDisplayOffsetEncodingAndBigEndianArePreserved()
    {
        const string json = """
        {
          "commhistory": ["tcp:1502"],
          "commstation": "7",
          "lastdbfile": "registers.bin",
          "regsmallendian": "0",
          "tbmode": "5",
          "tboffset": "400",
          "strencode": "UtF-8"
        }
        """;
        using JsonDocument document = JsonDocument.Parse(json);
        LegacySlaveConfiguration legacy = LegacySlaveConfigMigrator.Migrate(document.RootElement, "D:\\legacy");
        SlaveConfiguration configuration = legacy.Configuration;
        Assert.Equal(7, Assert.Single(configuration.Stations).UnitId);
        Assert.Equal(Path.GetFullPath("registers.bin", "D:\\legacy"), configuration.Stations[0].DataFilePath);
        Assert.True(configuration.UseTcp);
        Assert.False(configuration.UseSerial);
        Assert.Equal(1502, configuration.TcpPort);
        Assert.Equal(400, configuration.GridStartAddress);
        Assert.Equal("UTF-8", configuration.SelectedEncoding);
        Assert.Equal(ModbusDataType.Double, configuration.TableDataType);
        Assert.Equal(ModbusByteOrder.Order0123, configuration.TableByteOrder);
    }

    [Fact]
    public async Task UnsupportedNewSchemaIsRejected()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"slave-schema-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(filePath, "{ \"SchemaVersion\": 2 }");
            SlaveConfigurationManager manager = new();
            await Assert.ThrowsAsync<NotSupportedException>(async () => await manager.LoadAsync(filePath));
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
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
