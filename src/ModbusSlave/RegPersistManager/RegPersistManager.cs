// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Buffers.Binary;
using System.Security.Cryptography;
using ModbusCore.ModbusDataModel;

namespace ModbusSlave;

public sealed record RegisterPersistenceInfo(
    bool CreatedNew,
    bool LegacyImported,
    bool Rebuilt,
    string? BackupPath)
{
    public string? Warning => Rebuilt
        ? "寄存器文件校验失败，原文件已备份并创建空数据文件。"
        : null;
}

public sealed class RegPersistManager
{
    private const int LegacyFileSize = ModbusDataStore.AreaSize * 2;
    private const int BitAreaSize = ModbusDataStore.AreaSize / 8;
    private const int HeaderSize = 44;
    private const int PayloadSize = BitAreaSize * 2 + LegacyFileSize * 2;
    private static readonly byte[] Magic = "MBD2"u8.ToArray();

    public async ValueTask<RegisterPersistenceInfo> LoadAsync(
        string filePath,
        ModbusDataStore store,
        bool rebuildCorrupted = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(store);
        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            await SaveAsync(fullPath, store, cancellationToken).ConfigureAwait(false);
            return new RegisterPersistenceInfo(true, false, false, null);
        }

        try
        {
            long length = new FileInfo(fullPath).Length;
            if (length is not (LegacyFileSize) && length != HeaderSize + PayloadSize)
            {
                throw new InvalidDataException("The register file length is invalid.");
            }

            byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == LegacyFileSize)
            {
                ImportLegacy(store, bytes);
                return new RegisterPersistenceInfo(false, true, false, null);
            }

            ImportVersioned(store, bytes);
            return new RegisterPersistenceInfo(false, false, false, null);
        }
        catch (InvalidDataException) when (rebuildCorrupted)
        {
            string backupPath = fullPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(fullPath, backupPath);
            store.Clear();
            await SaveAsync(fullPath, store, cancellationToken).ConfigureAwait(false);
            return new RegisterPersistenceInfo(false, false, true, backupPath);
        }
    }

    public async ValueTask SaveAsync(
        string filePath,
        ModbusDataStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(store);
        string fullPath = Path.GetFullPath(filePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] payload = CreatePayload(store);
        byte[] bytes = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), payload.Length);
        SHA256.HashData(payload).CopyTo(bytes, 12);
        payload.CopyTo(bytes, HeaderSize);

        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16384,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] CreatePayload(ModbusDataStore store)
    {
        byte[] payload = new byte[PayloadSize];
        int offset = 0;
        WriteBits(payload, ref offset, store.ReadCoils(0, ModbusDataStore.AreaSize));
        WriteBits(payload, ref offset, store.ReadDiscreteInputs(0, ModbusDataStore.AreaSize));
        WriteRegisters(payload, ref offset, store.ReadHoldingRegisters(0, ModbusDataStore.AreaSize));
        WriteRegisters(payload, ref offset, store.ReadInputRegisters(0, ModbusDataStore.AreaSize));
        return payload;
    }

    private static void ImportVersioned(ModbusDataStore store, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.StartsWith(Magic))
        {
            throw new InvalidDataException("The register file header is invalid.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (version != 1 || payloadLength != PayloadSize || bytes.Length != HeaderSize + PayloadSize)
        {
            throw new InvalidDataException("The register file version or payload length is not supported.");
        }

        ReadOnlySpan<byte> payload = bytes.Slice(HeaderSize, PayloadSize);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(payload),
                bytes.Slice(12, 32)))
        {
            throw new InvalidDataException("The register file checksum is invalid.");
        }

        int offset = 0;
        store.WriteCoils(0, ReadBits(payload, ref offset));
        store.WriteDiscreteInputs(0, ReadBits(payload, ref offset));
        store.WriteHoldingRegisters(0, ReadRegisters(payload, ref offset));
        store.WriteInputRegisters(0, ReadRegisters(payload, ref offset));
    }

    private static void ImportLegacy(ModbusDataStore store, ReadOnlySpan<byte> bytes)
    {
        ushort[] registers = new ushort[ModbusDataStore.AreaSize];
        for (int index = 0; index < registers.Length; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(index * 2)..]);
        }

        store.WriteHoldingRegisters(0, registers);
    }

    private static void WriteBits(byte[] destination, ref int offset, ReadOnlySpan<bool> values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index])
            {
                destination[offset + index / 8] |= (byte)(1 << (index % 8));
            }
        }

        offset += BitAreaSize;
    }

    private static bool[] ReadBits(ReadOnlySpan<byte> source, ref int offset)
    {
        bool[] values = new bool[ModbusDataStore.AreaSize];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = (source[offset + index / 8] & (1 << (index % 8))) != 0;
        }

        offset += BitAreaSize;
        return values;
    }

    private static void WriteRegisters(byte[] destination, ref int offset, ReadOnlySpan<ushort> values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset + index * 2), values[index]);
        }

        offset += LegacyFileSize;
    }

    private static ushort[] ReadRegisters(ReadOnlySpan<byte> source, ref int offset)
    {
        ushort[] values = new ushort[ModbusDataStore.AreaSize];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt16LittleEndian(source[(offset + index * 2)..]);
        }

        offset += LegacyFileSize;
        return values;
    }
}
