// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusDataModel;

namespace ModbusCore.Tests;

public sealed class ModbusDataStoreTests
{
    [Fact]
    public void FourDataAreasAreIndependent()
    {
        using ModbusDataStore store = new();
        store.WriteCoils(10, [true, false]);
        store.WriteDiscreteInputs(10, [false, true]);
        store.WriteHoldingRegisters(10, [0x1122, 0x3344]);
        store.WriteInputRegisters(10, [0x5566, 0x7788]);

        Assert.Equal([true, false], store.ReadCoils(10, 2));
        Assert.Equal([false, true], store.ReadDiscreteInputs(10, 2));
        Assert.Equal([0x1122, 0x3344], store.ReadHoldingRegisters(10, 2));
        Assert.Equal([0x5566, 0x7788], store.ReadInputRegisters(10, 2));

        store.WriteSingleCoil(10, false);
        Assert.False(store.ReadCoils(10, 1)[0]);
        Assert.False(store.ReadDiscreteInputs(10, 1)[0]);
        Assert.Equal(0x1122, store.ReadHoldingRegisters(10, 1)[0]);
    }

    [Fact]
    public void LastAddressIsAccessibleWithoutOverflow()
    {
        using ModbusDataStore store = new();
        store.WriteSingleRegister(0xFFFF, 0xBEEF);
        store.WriteSingleCoil(0xFFFF, true);

        Assert.Equal(0xBEEF, store.ReadHoldingRegisters(0xFFFF, 1)[0]);
        Assert.True(store.ReadCoils(0xFFFF, 1)[0]);
    }

    [Fact]
    public void InvalidRangesAreRejected()
    {
        using ModbusDataStore store = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadCoils(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadHoldingRegisters(0xFFFF, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.WriteInputRegisters(0xFFFF, [1, 2]));
    }

    [Fact]
    public void WritesPublishAreaAddressAndCount()
    {
        using ModbusDataStore store = new();
        ModbusDataChangedEventArgs? changed = null;
        store.DataChanged += (_, args) => changed = args;

        store.WriteHoldingRegisters(100, [1, 2, 3]);

        Assert.NotNull(changed);
        Assert.Equal(ModbusDataArea.HoldingRegisters, changed.Area);
        Assert.Equal(100, changed.StartAddress);
        Assert.Equal(3, changed.Count);
    }

    [Fact]
    public void ReadsReturnSnapshotsInsteadOfInternalArrays()
    {
        using ModbusDataStore store = new();
        store.WriteHoldingRegisters(0, [1]);
        ushort[] first = store.ReadHoldingRegisters(0, 1);
        first[0] = 99;

        Assert.Equal(1, store.ReadHoldingRegisters(0, 1)[0]);
    }
}
