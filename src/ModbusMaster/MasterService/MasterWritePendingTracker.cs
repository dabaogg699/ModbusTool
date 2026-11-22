// © 2026 工控菜鸡刘 · ModbusTool · MIT License
using System.Collections.Concurrent;

namespace ModbusMaster;

/// <summary>
/// 按会话实例隔离的写入锁定表。锁定从写命令入队开始，到对应从站应答或失败为止；
/// 轮询刷新应先查询此表，避免旧值覆盖用户刚输入的预览值。
/// </summary>
public sealed class MasterWritePendingTracker
{
    private readonly ConcurrentDictionary<(byte UnitId, ushort Address), byte> _pending = new();

    public int Count => _pending.Count;

    public bool TryBegin(byte unitId, ushort address) => _pending.TryAdd((unitId, address), 0);

    public bool IsPending(byte unitId, ushort address) => _pending.ContainsKey((unitId, address));

    public bool Complete(byte unitId, ushort address) => _pending.TryRemove((unitId, address), out _);

    public void Clear() => _pending.Clear();
}
