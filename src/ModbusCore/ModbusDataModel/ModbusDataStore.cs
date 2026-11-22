// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusCore.ModbusDataModel;

public enum ModbusDataArea
{
    Coils,
    DiscreteInputs,
    HoldingRegisters,
    InputRegisters
}

public sealed class ModbusDataChangedEventArgs : EventArgs
{
    public ModbusDataChangedEventArgs(ModbusDataArea area, ushort startAddress, int count)
    {
        Area = area;
        StartAddress = startAddress;
        Count = count;
    }

    public ModbusDataArea Area { get; }

    public ushort StartAddress { get; }

    public int Count { get; }
}

public sealed class ModbusDataStore : IDisposable
{
    public const int AreaSize = 65536;

    private readonly bool[] _coils = new bool[AreaSize];
    private readonly bool[] _discreteInputs = new bool[AreaSize];
    private readonly ushort[] _holdingRegisters = new ushort[AreaSize];
    private readonly ushort[] _inputRegisters = new ushort[AreaSize];
    private readonly ReaderWriterLockSlim _lock = new();

    public event EventHandler<ModbusDataChangedEventArgs>? DataChanged;

    public bool[] ReadCoils(ushort startAddress, int count)
    {
        return ReadBits(_coils, startAddress, count);
    }

    public bool[] ReadDiscreteInputs(ushort startAddress, int count)
    {
        return ReadBits(_discreteInputs, startAddress, count);
    }

    public ushort[] ReadHoldingRegisters(ushort startAddress, int count)
    {
        return ReadRegisters(_holdingRegisters, startAddress, count);
    }

    public ushort[] ReadInputRegisters(ushort startAddress, int count)
    {
        return ReadRegisters(_inputRegisters, startAddress, count);
    }

    public void WriteCoils(ushort startAddress, ReadOnlySpan<bool> values)
    {
        WriteBits(_coils, ModbusDataArea.Coils, startAddress, values);
    }

    public void WriteDiscreteInputs(ushort startAddress, ReadOnlySpan<bool> values)
    {
        WriteBits(_discreteInputs, ModbusDataArea.DiscreteInputs, startAddress, values);
    }

    public void WriteHoldingRegisters(ushort startAddress, ReadOnlySpan<ushort> values)
    {
        WriteRegisters(_holdingRegisters, ModbusDataArea.HoldingRegisters, startAddress, values);
    }

    public void WriteInputRegisters(ushort startAddress, ReadOnlySpan<ushort> values)
    {
        WriteRegisters(_inputRegisters, ModbusDataArea.InputRegisters, startAddress, values);
    }

    public void WriteSingleCoil(ushort address, bool value)
    {
        WriteCoils(address, [value]);
    }

    public void WriteSingleRegister(ushort address, ushort value)
    {
        WriteHoldingRegisters(address, [value]);
    }

    public void Clear()
    {
        WriteCoils(0, new bool[AreaSize]);
        WriteDiscreteInputs(0, new bool[AreaSize]);
        WriteHoldingRegisters(0, new ushort[AreaSize]);
        WriteInputRegisters(0, new ushort[AreaSize]);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    private bool[] ReadBits(bool[] source, ushort startAddress, int count)
    {
        ValidateRange(startAddress, count);
        _lock.EnterReadLock();
        try
        {
            return source.AsSpan(startAddress, count).ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private ushort[] ReadRegisters(ushort[] source, ushort startAddress, int count)
    {
        ValidateRange(startAddress, count);
        _lock.EnterReadLock();
        try
        {
            return source.AsSpan(startAddress, count).ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private void WriteBits(
        bool[] destination,
        ModbusDataArea area,
        ushort startAddress,
        ReadOnlySpan<bool> values)
    {
        ValidateRange(startAddress, values.Length);
        _lock.EnterWriteLock();
        try
        {
            values.CopyTo(destination.AsSpan(startAddress));
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        OnDataChanged(area, startAddress, values.Length);
    }

    private void WriteRegisters(
        ushort[] destination,
        ModbusDataArea area,
        ushort startAddress,
        ReadOnlySpan<ushort> values)
    {
        ValidateRange(startAddress, values.Length);
        _lock.EnterWriteLock();
        try
        {
            values.CopyTo(destination.AsSpan(startAddress));
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        OnDataChanged(area, startAddress, values.Length);
    }

    private void OnDataChanged(ModbusDataArea area, ushort startAddress, int count)
    {
        DataChanged?.Invoke(this, new ModbusDataChangedEventArgs(area, startAddress, count));
    }

    private static void ValidateRange(ushort startAddress, int count)
    {
        if (count < 1 || startAddress + count > AreaSize)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "The address range is invalid.");
        }
    }
}
