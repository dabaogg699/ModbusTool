// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Buffers.Binary;
using ModbusCore.ModbusDataModel;

namespace ModbusSlave.Tests;

public sealed class RegPersistManagerTests
{
    [Fact]
    public async Task VersionedFileRoundTripsAllFourAreas()
    {
        string filePath = TempFilePath();
        using ModbusDataStore source = new();
        source.WriteCoils(0, [true]);
        source.WriteCoils(65535, [true]);
        source.WriteDiscreteInputs(10, [true]);
        source.WriteHoldingRegisters(100, [0x1234]);
        source.WriteInputRegisters(200, [0xABCD]);
        RegPersistManager manager = new();
        try
        {
            await manager.SaveAsync(filePath, source);
            using ModbusDataStore loaded = new();
            RegisterPersistenceInfo info = await manager.LoadAsync(filePath, loaded);

            Assert.False(info.CreatedNew);
            Assert.False(info.LegacyImported);
            Assert.False(info.Rebuilt);
            Assert.True(loaded.ReadCoils(0, 1)[0]);
            Assert.True(loaded.ReadCoils(65535, 1)[0]);
            Assert.True(loaded.ReadDiscreteInputs(10, 1)[0]);
            Assert.Equal(0x1234, loaded.ReadHoldingRegisters(100, 1)[0]);
            Assert.Equal(0xABCD, loaded.ReadInputRegisters(200, 1)[0]);
            Assert.Equal("MBD2", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(filePath).AsSpan(0, 4)));
        }
        finally
        {
            DeleteFile(filePath);
        }
    }

    [Fact]
    public async Task RealLegacyDataFileImportsAllRegistersWithoutChangingSource()
    {
        string sourcePath = GetRepositoryFile("tests", "fixtures", "legacy", "MBSlave", "data.bin");
        byte[] originalBytes = await File.ReadAllBytesAsync(sourcePath);
        using ModbusDataStore store = new();
        RegPersistManager manager = new();

        RegisterPersistenceInfo info = await manager.LoadAsync(sourcePath, store);

        Assert.True(info.LegacyImported);
        Assert.False(info.Rebuilt);
        ushort[] expected = new ushort[ModbusDataStore.AreaSize];
        for (int index = 0; index < expected.Length; index++)
        {
            expected[index] = BinaryPrimitives.ReadUInt16LittleEndian(originalBytes.AsSpan(index * 2));
        }

        Assert.Equal(expected, store.ReadHoldingRegisters(0, ModbusDataStore.AreaSize));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
    }

    [Fact]
    public async Task LegacyRawFileImportsLittleEndianHoldingRegisters()
    {
        string filePath = TempFilePath();
        byte[] legacy = new byte[ModbusDataStore.AreaSize * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(legacy, 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(2), 0xABCD);
        BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(legacy.Length - 2), 0xBEEF);
        await File.WriteAllBytesAsync(filePath, legacy);
        using ModbusDataStore store = new();
        RegPersistManager manager = new();
        try
        {
            RegisterPersistenceInfo info = await manager.LoadAsync(filePath, store);

            Assert.True(info.LegacyImported);
            Assert.Equal([0x1234, 0xABCD], store.ReadHoldingRegisters(0, 2));
            Assert.Equal(0xBEEF, store.ReadHoldingRegisters(65535, 1)[0]);
            Assert.Equal(0, store.ReadInputRegisters(0, 1)[0]);
            Assert.False(store.ReadCoils(0, 1)[0]);
        }
        finally
        {
            DeleteFile(filePath);
        }
    }

    [Fact]
    public async Task CorruptFileIsBackedUpAndAutomaticallyRebuilt()
    {
        string filePath = TempFilePath();
        using ModbusDataStore store = new();
        store.WriteHoldingRegisters(0, [0xFFFF]);
        RegPersistManager manager = new();
        string? backupPath = null;
        try
        {
            await manager.SaveAsync(filePath, store);
            byte[] corrupted = await File.ReadAllBytesAsync(filePath);
            corrupted[100] ^= 0xFF;
            await File.WriteAllBytesAsync(filePath, corrupted);

            RegisterPersistenceInfo info = await manager.LoadAsync(filePath, store);
            backupPath = info.BackupPath;

            Assert.True(info.Rebuilt);
            Assert.NotNull(info.Warning);
            Assert.True(File.Exists(backupPath));
            Assert.Equal(0, store.ReadHoldingRegisters(0, 1)[0]);
        }
        finally
        {
            DeleteFile(filePath);
            DeleteFile(backupPath);
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

        throw new FileNotFoundException("找不到原始数据样例。", Path.Combine(parts));
    }

    private static string TempFilePath()
    {
        return Path.Combine(Path.GetTempPath(), $"modbus-registers-{Guid.NewGuid():N}.bin");
    }

    private static void DeleteFile(string? filePath)
    {
        if (filePath is not null && File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
