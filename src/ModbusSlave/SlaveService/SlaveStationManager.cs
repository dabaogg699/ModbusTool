// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using ModbusCore.ModbusDataModel;

namespace ModbusSlave;

public sealed class SlaveStationManager : IDisposable
{
    private readonly Dictionary<byte, ModbusDataStore> _stations = [];
    private readonly List<ModbusDataStore> _allStores = [];
    private readonly object _sync = new();
    private bool _disposed;

    public SlaveStationManager(IEnumerable<byte>? unitIds = null)
    {
        foreach (byte unitId in unitIds ?? [1])
        {
            AddStation(unitId);
        }
    }

    public IReadOnlyCollection<byte> UnitIds
    {
        get
        {
            lock (_sync)
            {
                return _stations.Keys.OrderBy(static unitId => unitId).ToArray();
            }
        }
    }

    public ModbusDataStore GetOrAddStation(byte unitId)
    {
        ValidateUnitId(unitId);
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_stations.TryGetValue(unitId, out ModbusDataStore? existing))
            {
                return existing;
            }

            ModbusDataStore store = new();
            _stations.Add(unitId, store);
            _allStores.Add(store);
            return store;
        }
    }

    public bool AddStation(byte unitId)
    {
        ValidateUnitId(unitId);
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            if (_stations.ContainsKey(unitId))
            {
                return false;
            }

            ModbusDataStore store = new();
            _stations.Add(unitId, store);
            _allStores.Add(store);
            return true;
        }
    }

    public bool RemoveStation(byte unitId)
    {
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            return _stations.Remove(unitId);
        }
    }

    public bool TryGetStation(byte unitId, out ModbusDataStore? store)
    {
        lock (_sync)
        {
            ArgumentGuard.ThrowIfDisposed(_disposed, this);
            return _stations.TryGetValue(unitId, out store);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (ModbusDataStore store in _allStores)
            {
                store.Dispose();
            }

            _stations.Clear();
            _allStores.Clear();
        }
    }

    private static void ValidateUnitId(byte unitId)
    {
        if (unitId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitId), "Unit ID zero is reserved for broadcasts.");
        }
    }
}
