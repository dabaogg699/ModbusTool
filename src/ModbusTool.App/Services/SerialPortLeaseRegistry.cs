// © 2026 工控菜鸡刘 · ModbusTool · MIT License
namespace ModbusTool.App.Services;

public static class SerialPortLeaseRegistry
{
    private static readonly Dictionary<string, (object Owner, string SessionName)> Leases =
        new(StringComparer.OrdinalIgnoreCase);

    public static string? GetOwnerSession(string portName)
    {
        string normalized = (portName ?? string.Empty).Trim();
        return Leases.TryGetValue(normalized, out (object Owner, string SessionName) lease) ? lease.SessionName : null;
    }

    public static bool IsOwnedBy(string portName, object owner)
    {
        string normalized = (portName ?? string.Empty).Trim();
        return Leases.TryGetValue(normalized, out (object Owner, string SessionName) lease)
            && ReferenceEquals(lease.Owner, owner);
    }

    public static void Acquire(string portName, object owner, string sessionName)
    {
        string normalized = (portName ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("串口名称不能为空。");
        }

        if (Leases.TryGetValue(normalized, out (object Owner, string SessionName) lease)
            && !ReferenceEquals(lease.Owner, owner))
        {
            throw new InvalidOperationException(
                $"串口 {normalized} 已被会话“{lease.SessionName}”占用，请更换串口或先停止该会话。");
        }

        Leases[normalized] = (owner, sessionName);
    }

    public static void Release(object owner)
    {
        foreach (string port in Leases
            .Where(pair => ReferenceEquals(pair.Value.Owner, owner))
            .Select(pair => pair.Key)
            .ToArray())
        {
            Leases.Remove(port);
        }
    }
}
